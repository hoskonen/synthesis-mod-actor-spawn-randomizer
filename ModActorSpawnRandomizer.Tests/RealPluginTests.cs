using Microsoft.VisualStudio.TestTools.UnitTesting;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Cache;
using Mutagen.Bethesda.Skyrim;

namespace ModActorSpawnRandomizer.Tests;

/// <summary>
/// Validation against the plugins investigated in docs/01-feasibility.md.
/// Runs only when these environment variables are set (otherwise inconclusive):
///   MASR_GAME_DATA   Skyrim SE Data folder (masters)
///   MASR_MODS_DIR    folder containing the mod plugins (e.g. an MO2 "mods" folder; searched two levels deep)
/// Optional:
///   MASR_MO2_PROFILE MO2 profile folder, to check in-game FormIDs against the real load order
///   MASR_REPORT_OUT  file path to write the full dry-run report to
/// </summary>
[TestClass]
public sealed class RealPluginTests
{
    private const string Mourning = "mihailmourningsouls.esp";
    private const string Bats = "mihailbats.esp";
    private const string Specters = "SpectersOfSkyrim.esp";
    private const string Hpe = "Hand Placed Enemies.esp";

    private static readonly string[] Masters = ["Skyrim.esm", "Update.esm", "Dawnguard.esm", "HearthFires.esm", "Dragonborn.esm"];

    private static IReadOnlyDictionary<string, PluginAnalysis>? _results;
    private static string? _skipReason;

    [ClassInitialize]
    public static void Load(TestContext _)
    {
        var data = Environment.GetEnvironmentVariable("MASR_GAME_DATA");
        var mods = Environment.GetEnvironmentVariable("MASR_MODS_DIR");
        if (string.IsNullOrEmpty(data) || string.IsNullOrEmpty(mods) || !Directory.Exists(data) || !Directory.Exists(mods))
        {
            _skipReason = "Set MASR_GAME_DATA and MASR_MODS_DIR to run real-plugin validation.";
            return;
        }

        var loadOrder = new List<ISkyrimModGetter>();
        foreach (var master in Masters)
            loadOrder.Add(SkyrimMod.CreateFromBinaryOverlay(Path.Combine(data, master), SkyrimRelease.SkyrimSE));
        foreach (var plugin in new[] { Mourning, Hpe, Specters, Bats })
        {
            var path = FindPlugin(mods, plugin);
            if (path is null)
            {
                _skipReason = $"{plugin} not found under MASR_MODS_DIR.";
                return;
            }

            loadOrder.Add(SkyrimMod.CreateFromBinaryOverlay(path, SkyrimRelease.SkyrimSE));
        }

        var settings = new Settings
        {
            Seed = 38174,
            DetailedReport = true,
            Plugins =
            [
                new PluginSelection { Plugin = ModKey.FromFileName(Mourning), Probability = 30 },
                new PluginSelection { Plugin = ModKey.FromFileName(Bats), Probability = 50 },
                new PluginSelection { Plugin = ModKey.FromFileName(Specters), Probability = 5 },
                new PluginSelection { Plugin = ModKey.FromFileName(Hpe), Probability = 60 },
            ],
        };

        _loadOrder = loadOrder;
        var analyses = DryRun.Analyze(settings, loadOrder, loadOrder.ToImmutableLinkCache());
        _results = analyses.ToDictionary(a => a.Plugin.FileName.String);

        var layout = Mo2Layout(data, mods) ?? LoadOrderLayout.FromMods(loadOrder);
        _layout = layout;
        var reportPath = Environment.GetEnvironmentVariable("MASR_REPORT_OUT");
        if (!string.IsNullOrEmpty(reportPath))
            File.WriteAllText(reportPath, DryRunReport.Render(analyses, layout, settings.Seed, detailed: true));
    }

    private static LoadOrderLayout? _layout;
    private static IReadOnlyList<ISkyrimModGetter>? _loadOrder;

    /// <summary>
    /// The configuration of the first GUI dry run (Bats, Specters, Mourning Souls at 50%, seed 38174):
    /// apply it, write the ESP to disk, read it back and check every record.
    /// </summary>
    [TestMethod]
    public void ApplyReproducesFirstGuiRunAndWritesOnlyExpectedRecords()
    {
        _ = Get(Bats);
        var settings = new Settings
        {
            Seed = 38174,
            DryRun = false,
            Plugins =
            [
                new PluginSelection { Plugin = ModKey.FromFileName(Bats), Probability = 50 },
                new PluginSelection { Plugin = ModKey.FromFileName(Specters), Probability = 50 },
                new PluginSelection { Plugin = ModKey.FromFileName(Mourning), Probability = 50 },
            ],
        };
        var patchKey = ModKey.FromFileName("ModActorSpawnRandomizer.esp");
        var patch = new SkyrimMod(patchKey, SkyrimRelease.SkyrimSE);
        var cache = _loadOrder!.ToImmutableLinkCache();

        var outcome = PatcherRun.Execute(settings, _loadOrder!, cache, patch);

        var perPlugin = outcome.Apply!.Plugins.ToDictionary(p => p.Plugin.FileName.String);
        Assert.AreEqual(51, perPlugin[Bats].OverridesWritten);
        Assert.AreEqual(88, perPlugin[Specters].OverridesWritten);
        Assert.AreEqual(11, perPlugin[Mourning].OverridesWritten);
        Assert.AreEqual(150, outcome.Apply.TotalWritten);
        // Every rejected specter carries AutomaticLightSwitchScript, which would enable it again at night.
        Assert.AreEqual(44, perPlugin[Specters].ScriptsRemoved);
        Assert.AreEqual(0, perPlugin[Bats].ScriptsRemoved);
        Assert.AreEqual(0, perPlugin[Mourning].ScriptsRemoved);

        var path = Path.Combine(Path.GetTempPath(), $"masr-{Guid.NewGuid():N}", patchKey.FileName);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        try
        {
            patch.WriteToBinary(path);
            using var written = SkyrimMod.CreateFromBinaryOverlay(path, SkyrimRelease.SkyrimSE);

            var achrs = written.EnumerateMajorRecords<IPlacedNpcGetter>().ToList();
            Assert.AreEqual(150, achrs.Count);
            Assert.IsTrue(achrs.All(r => r.MajorFlags.HasFlag(PlacedNpc.MajorFlag.InitiallyDisabled)));
            Assert.IsTrue(achrs.All(r => r.VirtualMachineAdapter is null), "no disabled placement may keep a reference script");
            var rejected = outcome.Analyses.SelectMany(a => a.Units)
                .Where(u => u.Decision == SelectionDecision.Rejected)
                .SelectMany(u => u.Members)
                .ToHashSet();
            Assert.IsTrue(rejected.SetEquals(achrs.Select(r => r.FormKey)));

            var types = written.EnumerateMajorRecords().GroupBy(r => r switch
            {
                IPlacedNpcGetter => "ACHR",
                ICellGetter => "CELL",
                IWorldspaceGetter => "WRLD",
                _ => r.GetType().Name,
            }).ToDictionary(g => g.Key, g => g.Count());
            CollectionAssert.AreEquivalent(new[] { "ACHR", "CELL", "WRLD" }, types.Keys.ToArray(), string.Join(", ", types));

            // Every written ACHR equals its winning record except for the InitiallyDisabled flag.
            foreach (var achr in achrs)
            {
                var expected = cache.Resolve<IPlacedNpcGetter>(achr.FormKey).DeepCopy();
                expected.MajorFlags |= PlacedNpc.MajorFlag.InitiallyDisabled;
                expected.VirtualMachineAdapter = null;
                Assert.IsTrue(expected.Equals(achr), $"{achr.FormKey} differs from its winning record");
            }

            // Specters pairs stay atomic: both or neither member of every pair is disabled.
            var specterUnits = outcome.Analyses.Single(a => a.Plugin.FileName == Specters).Units;
            var disabled = achrs.Select(r => r.FormKey).ToHashSet();
            Assert.IsTrue(specterUnits.All(u => u.Members.Count(disabled.Contains) is 0 or 2));
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
        }
    }

    [TestMethod]
    public void ApplyNeverTouchesHpeGroupsNeedingInvestigation()
    {
        _ = Get(Hpe);
        var settings = new Settings
        {
            Seed = 38174,
            DryRun = false,
            Plugins = [new PluginSelection { Plugin = ModKey.FromFileName(Hpe), Probability = 0 }],
        };
        var patch = new SkyrimMod(ModKey.FromFileName("ModActorSpawnRandomizer.esp"), SkyrimRelease.SkyrimSE);

        var outcome = PatcherRun.Execute(settings, _loadOrder!, _loadOrder!.ToImmutableLinkCache(), patch);

        var disabled = patch.EnumerateMajorRecords<IPlacedNpcGetter>().Select(r => r.FormKey).ToHashSet();
        var analysis = outcome.Analyses.Single();
        var selectable = analysis.Placements.Where(p => p.Status is PlacementStatus.Eligible or PlacementStatus.Grouped).ToList();
        Assert.AreEqual(selectable.Count, disabled.Count, "0% disables every selectable placement");
        Assert.IsTrue(analysis.Placements
            .Where(p => p.Status is PlacementStatus.Excluded or PlacementStatus.NeedsInvestigation)
            .All(p => !disabled.Contains(p.Facts.FormKey)));
        foreach (var id in new uint[] { 0xAFB, 0xAFC, 0xAFD, 0xAFE, 0xAFF })
            Assert.IsFalse(disabled.Contains(new FormKey(ModKey.FromFileName(Hpe), id)), "Bleak Falls ambush must stay untouched");
        Assert.IsFalse(analysis.Placements.Any(p => p.Facts.EnableParent is not null && disabled.Contains(p.Facts.FormKey)));
    }

    private static PluginAnalysis Get(string plugin)
    {
        if (_skipReason is not null) Assert.Inconclusive(_skipReason);
        return _results![plugin];
    }

    private static int Count(PluginAnalysis a, PlacementStatus s) => a.Placements.Count(p => p.Status == s);

    private static int WithReason(PluginAnalysis a, PlacementReason r) => a.Placements.Count(p => p.Reasons.Contains(r));

    private static AssessedPlacement At(PluginAnalysis a, uint id) => a.Placements.Single(p => p.Facts.FormKey.ID == id);

    [TestMethod]
    public void MourningSouls()
    {
        var a = Get(Mourning);
        Assert.AreEqual(18, a.Placements.Count);
        Assert.AreEqual(0, a.OverridesOfOtherPlacements);
        Assert.IsTrue(a.Placements.All(p => !p.Facts.Cell.Interior && p.Facts.Cell.Worldspace == "DLC01SoulCairn"));
        Assert.IsTrue(a.Placements.All(p => p.Status is PlacementStatus.Eligible or PlacementStatus.Grouped),
            "all outward-linked ambush placements are eligible");
        Assert.IsTrue(a.Placements.All(p => p.Facts.InboundReferences.Count == 0));

        // The pair sharing trigger 00FB3F is the only encounter group.
        var group = a.Units.Single(u => u.IsGroup);
        CollectionAssert.AreEquivalent(new uint[] { 0x00FB3B, 0x00FB3C }, group.Members.Select(m => m.ID).ToArray());
        Assert.IsTrue(group.Kinds.HasFlag(GroupKinds.SharedLinkTarget));
        Assert.AreEqual(PluginLayout.Grouped, a.Layout);
        Assert.IsFalse(a.HasWarnings);
    }

    [TestMethod]
    public void EnemyBats()
    {
        var a = Get(Bats);
        Assert.AreEqual(101, a.Placements.Count);
        Assert.AreEqual(101, Count(a, PlacementStatus.Eligible));
        Assert.IsTrue(a.Placements.All(p => p.Facts.Cell.Interior));
        Assert.AreEqual(0, a.Units.Count(u => u.IsGroup));
        Assert.AreEqual(PluginLayout.Simple, a.Layout);
        Assert.IsFalse(a.HasWarnings);
    }

    [TestMethod]
    public void SpectersOfSkyrim()
    {
        var a = Get(Specters);
        Assert.AreEqual(186, a.Placements.Count);
        Assert.AreEqual(93, a.Placements.Count(p => p.Facts.Scripts.Any(s => s.Name == "AutomaticLightSwitchScript")));

        // 93 exact-position specter + skeleton pairs, all selectable as pairs.
        var groups = a.Units.Where(u => u.IsGroup).ToList();
        Assert.AreEqual(93, groups.Count);
        Assert.IsTrue(groups.All(g => g.Members.Count == 2 && g.Kinds == GroupKinds.ExactPosition));
        Assert.AreEqual(186, Count(a, PlacementStatus.Grouped));
        Assert.AreEqual(PluginLayout.Grouped, a.Layout);
        Assert.IsFalse(a.HasWarnings);
    }

    [TestMethod]
    public void HandPlacedEnemies()
    {
        var a = Get(Hpe);
        Assert.AreEqual(1557, a.Placements.Count);
        Assert.AreEqual(16, a.OverridesOfOtherPlacements);
        Assert.AreEqual(1200, a.Placements.Count(p => p.Facts.Cell.Interior));

        // Every enable-parented placement stays untouched until the in-game test (feasibility section 9).
        var enableParented = a.Placements.Where(p => p.Facts.EnableParent is not null).ToList();
        Assert.AreEqual(343, enableParented.Count);
        Assert.IsTrue(enableParented.All(p => p.Status is PlacementStatus.NeedsInvestigation or PlacementStatus.Excluded));
        Assert.AreEqual(63, enableParented.Select(p => p.Facts.EnableParent!.Target).Distinct().Count());

        // Documented safety exclusions.
        Assert.AreEqual(11, WithReason(a, PlacementReason.BossLocationRefType));
        Assert.AreEqual(3, WithReason(a, PlacementReason.QuestScript));
        Assert.AreEqual(2, WithReason(a, PlacementReason.DeathCounterScript));
        Assert.AreEqual(3, WithReason(a, PlacementReason.OppositeEnableParent));
        Assert.AreEqual(23, WithReason(a, PlacementReason.AlreadyDisabled));
        Assert.AreEqual(18, WithReason(a, PlacementReason.PersistentReference));

        // Bleak Falls Barrow ambush (verified in xEdit): five bandits, enable parent 000B01.
        var ambush = a.Units.Single(u => u.Members.Any(m => m.ID == 0x000AFB));
        CollectionAssert.AreEquivalent(new uint[] { 0xAFB, 0xAFC, 0xAFD, 0xAFE, 0xAFF }, ambush.Members.Select(m => m.ID).ToArray());
        Assert.AreEqual(PlacementStatus.NeedsInvestigation, ambush.Status);
        Assert.AreEqual(SelectionDecision.NotApplicable, ambush.Decision);
        Assert.AreEqual(new FormKey(ModKey.FromFileName(Hpe), 0x000B01), At(a, 0xAFB).Facts.EnableParent!.Target);

        // Plain bandit near the entrance is selectable.
        Assert.AreNotEqual(SelectionDecision.NotApplicable, a.Units[At(a, 0xAF5).UnitIndex].Decision);

        Assert.AreEqual(PluginLayout.Grouped, a.Layout);
        Assert.IsTrue(a.HasWarnings);
    }

    [TestMethod]
    public void InGameFormIdsMatchXEdit()
    {
        _ = Get(Hpe);
        if (Environment.GetEnvironmentVariable("MASR_MO2_PROFILE") is not { Length: > 0 })
            Assert.Inconclusive("Set MASR_MO2_PROFILE to check in-game FormIDs.");
        // Confirmed in xEdit: HPE is in light slot FE:04D.
        Assert.AreEqual("FE04DAFB", _layout!.InGameFormId(new FormKey(ModKey.FromFileName(Hpe), 0x000AFB)));
    }

    [TestMethod]
    public void AnalysisIsRepeatable()
    {
        var first = Get(Bats);
        var again = PluginAnalyzer.Analyze(
            first.Plugin,
            first.Placements.Select(p => p.Facts).Reverse().ToList(),
            first.OverridesOfOtherPlacements,
            first.Probability,
            first.Seed);
        CollectionAssert.AreEqual(first.Units.Select(u => u.Decision).ToList(), again.Units.Select(u => u.Decision).ToList());
    }

    private static string? FindPlugin(string modsDir, string fileName)
    {
        var direct = Path.Combine(modsDir, fileName);
        if (File.Exists(direct)) return direct;
        return Directory.EnumerateDirectories(modsDir)
            .Select(d => Path.Combine(d, fileName))
            .FirstOrDefault(File.Exists);
    }

    /// <summary>Builds the runtime layout from an MO2 profile (loadorder.txt, plugins.txt, Skyrim.ccc).</summary>
    private static LoadOrderLayout? Mo2Layout(string data, string mods)
    {
        var profile = Environment.GetEnvironmentVariable("MASR_MO2_PROFILE");
        if (string.IsNullOrEmpty(profile) || !Directory.Exists(profile)) return null;

        var active = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        active.UnionWith(Masters);
        var ccc = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(data).TrimEnd('\\', '/'))!, "Skyrim.ccc");
        if (File.Exists(ccc)) active.UnionWith(File.ReadAllLines(ccc).Select(l => l.Trim()).Where(l => l.Length > 0));
        active.UnionWith(File.ReadAllLines(Path.Combine(profile, "plugins.txt"))
            .Where(l => l.StartsWith('*'))
            .Select(l => l[1..].Trim()));

        var entries = new List<(ModKey, bool)>();
        foreach (var line in File.ReadAllLines(Path.Combine(profile, "loadorder.txt")))
        {
            var name = line.Trim();
            if (name.Length == 0 || name.StartsWith('#') || !active.Contains(name)) continue;
            var path = File.Exists(Path.Combine(data, name)) ? Path.Combine(data, name) : FindPlugin(mods, name);
            if (path is null) continue; // listed but not installed: takes no slot
            entries.Add((ModKey.FromFileName(name), name.EndsWith(".esl", StringComparison.OrdinalIgnoreCase) || HasLightFlag(path)));
        }

        return new LoadOrderLayout(entries);
    }

    private static bool HasLightFlag(string pluginPath)
    {
        using var stream = File.OpenRead(pluginPath);
        Span<byte> header = stackalloc byte[12];
        stream.ReadExactly(header);
        return (BitConverter.ToUInt32(header[8..12]) & 0x200) != 0;
    }
}
