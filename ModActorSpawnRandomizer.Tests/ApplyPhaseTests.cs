using Microsoft.VisualStudio.TestTools.UnitTesting;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using Noggog;

namespace ModActorSpawnRandomizer.Tests;

[TestClass]
public sealed class ApplyPhaseTests
{
    private static readonly ModKey MasterKey = ModKey.FromFileName("Master.esm");
    private static readonly ModKey CreatureKey = ModKey.FromFileName("Creatures.esp");
    private static readonly ModKey LaterKey = ModKey.FromFileName("Later.esp");

    /// <summary>Master with a dungeon cell, a creature plugin placing NPCs in it, and an optional later plugin.</summary>
    private sealed class World
    {
        public SkyrimMod Master { get; } = new(MasterKey, SkyrimRelease.SkyrimSE);
        public SkyrimMod Creatures { get; } = new(CreatureKey, SkyrimRelease.SkyrimSE);
        public SkyrimMod Later { get; } = new(LaterKey, SkyrimRelease.SkyrimSE);
        public Cell MasterCell { get; }
        public Cell CreatureCell { get; }
        public Npc Creature { get; }
        public LocationReferenceType Boss { get; }

        public World()
        {
            MasterCell = new Cell(Master) { EditorID = "Dungeon01", Flags = Cell.Flag.IsInteriorCell };
            Master.Cells.Records.Add(Block(MasterCell));
            Boss = new LocationReferenceType(Master) { EditorID = "Boss" };
            Master.LocationReferenceTypes.Add(Boss);
            Creature = new Npc(Creatures) { EditorID = "TestCreature" };
            Creatures.Npcs.Add(Creature);
            CreatureCell = new Cell(MasterCell.FormKey, SkyrimRelease.SkyrimSE) { EditorID = "Dungeon01", Flags = Cell.Flag.IsInteriorCell };
            Creatures.Cells.Records.Add(Block(CreatureCell));
            Creatures.ModHeader.MasterReferences.Add(new MasterReference { Master = MasterKey });
            Later.ModHeader.MasterReferences.Add(new MasterReference { Master = MasterKey });
            Later.ModHeader.MasterReferences.Add(new MasterReference { Master = CreatureKey });
        }

        public PlacedNpc Place(float x, Action<PlacedNpc>? configure = null)
        {
            var placed = new PlacedNpc(Creatures)
            {
                Base = Creature.ToNullableLink(),
                Placement = new Placement { Position = new P3Float(x, 0, 0) },
            };
            configure?.Invoke(placed);
            CreatureCell.Temporary.Add(placed);
            return placed;
        }

        public List<PlacedNpc> PlacePlain(int count) =>
            Enumerable.Range(0, count).Select(i => Place(i * 100f)).ToList();

        /// <summary>Adds a later-plugin override of a placement (copy of the creature plugin's record).</summary>
        public PlacedNpc OverrideInLater(PlacedNpc original, Action<PlacedNpc> change)
        {
            var copy = original.DeepCopy();
            change(copy);
            var cell = Later.Cells.Records.SelectMany(b => b.SubBlocks).SelectMany(s => s.Cells).FirstOrDefault();
            if (cell is null)
            {
                cell = new Cell(MasterCell.FormKey, SkyrimRelease.SkyrimSE) { Flags = Cell.Flag.IsInteriorCell };
                Later.Cells.Records.Add(Block(cell));
            }

            cell.Temporary.Add(copy);
            return copy;
        }

        public ISkyrimModGetter[] LoadOrder(bool withLater = false) =>
            withLater ? [Master, Creatures, Later] : [Master, Creatures];

        public static CellBlock Block(Cell cell) => new()
        {
            BlockNumber = 0,
            GroupType = GroupTypeEnum.InteriorCellBlock,
            SubBlocks = { new CellSubBlock { BlockNumber = 0, GroupType = GroupTypeEnum.InteriorCellSubBlock, Cells = { cell } } },
        };
    }

    private static Settings Settings(double probability, bool dryRun = false, int seed = 38174) => new()
    {
        Seed = seed,
        DryRun = dryRun,
        Plugins = [new PluginSelection { Plugin = CreatureKey, Probability = probability }],
    };

    private static (PatcherRun.Outcome Outcome, SkyrimMod Patch) Run(World world, Settings settings, bool withLater = false)
    {
        var loadOrder = world.LoadOrder(withLater);
        var patch = new SkyrimMod(ModKey.FromFileName("Synthesis.esp"), SkyrimRelease.SkyrimSE);
        var outcome = PatcherRun.Execute(settings, loadOrder, loadOrder.ToImmutableLinkCache(), patch);
        Assert.IsNull(outcome.Failure, outcome.Failure);
        return (outcome, patch);
    }

    private static HashSet<FormKey> Overrides(ISkyrimModGetter patch) =>
        patch.EnumerateMajorRecords<IPlacedNpcGetter>().Select(r => r.FormKey).ToHashSet();

    private static HashSet<FormKey> Rejected(PatcherRun.Outcome outcome) =>
        outcome.Analyses.SelectMany(a => a.Units)
            .Where(u => u.Decision == SelectionDecision.Rejected)
            .SelectMany(u => u.Members)
            .ToHashSet();

    [TestMethod]
    public void DryRunCreatesNoRecords()
    {
        var world = new World();
        world.PlacePlain(30);
        var (outcome, patch) = Run(world, Settings(50, dryRun: true));
        Assert.IsTrue(Rejected(outcome).Count > 0, "fixture must have rejected units");
        Assert.AreEqual(0, patch.EnumerateMajorRecords().Count());
        Assert.IsNull(outcome.Apply);
        StringAssert.Contains(outcome.Report, "DRY RUN (read-only");
    }

    [TestMethod]
    public void ApplyDisablesExactlyTheRejectedEligiblePlacements()
    {
        var world = new World();
        var plain = world.PlacePlain(40);
        var boss = world.Place(9000, p => p.LocationRefTypes = new ExtendedList<IFormLinkGetter<ILocationReferenceTypeGetter>> { world.Boss.ToLink() });
        var marker = new PlacedObject(world.Creatures) { MajorRecordFlagsRaw = 0xC00 };
        world.CreatureCell.Persistent.Add(marker);
        var ambush = world.Place(9100, p => p.EnableParent = new EnableParent { Reference = marker.ToLink<IPlacedGetter>() });

        var (outcome, patch) = Run(world, Settings(50));

        var rejected = Rejected(outcome);
        CollectionAssert.AreEquivalent(rejected.ToList(), Overrides(patch).ToList());
        var selected = outcome.Analyses.Single().Units.Where(u => u.Decision == SelectionDecision.Selected).SelectMany(u => u.Members);
        Assert.IsFalse(selected.Any(Overrides(patch).Contains), "selected placements must not be overridden");
        Assert.IsFalse(Overrides(patch).Contains(boss.FormKey), "excluded placement must be untouched");
        Assert.IsFalse(Overrides(patch).Contains(ambush.FormKey), "needs-investigation placement must be untouched");
        Assert.AreEqual(outcome.Apply!.TotalPlanned, outcome.Apply.TotalWritten);
        StringAssert.Contains(outcome.Report, "APPLY COMPLETE");
        StringAssert.Contains(outcome.Report, "(reconciled)");
        _ = plain;
    }

    [TestMethod]
    public void UnscriptedOverrideIsWinningRecordPlusInitiallyDisabledOnly()
    {
        var world = new World();
        var keyword = new Keyword(world.Master) { EditorID = "LinkKw" };
        world.Master.Keywords.Add(keyword);
        var idle = new PlacedObject(world.Creatures) { MajorRecordFlagsRaw = 0x400 };
        world.CreatureCell.Persistent.Add(idle);
        var original = world.Place(1, p =>
        {
            p.LevelModifier = Level.Hard;
            p.Scale = 1.5f;
            p.LinkedReferences.Add(new LinkedReferences { Reference = idle.ToLink<IPlacedGetter>(), KeywordOrReference = keyword.ToLink<IKeywordLinkedReferenceGetter>() });
        });
        // The winning record comes from a later plugin that moved the placement.
        var winning = world.OverrideInLater(original, p => p.Placement = new Placement { Position = new P3Float(1, 2, 3), Rotation = new P3Float(0, 0, 1) });

        var (_, patch) = Run(world, Settings(0), withLater: true);

        var written = patch.EnumerateMajorRecords<IPlacedNpcGetter>().Single();
        Assert.IsTrue(written.MajorFlags.HasFlag(PlacedNpc.MajorFlag.InitiallyDisabled));
        var expected = winning.DeepCopy();
        expected.MajorFlags |= PlacedNpc.MajorFlag.InitiallyDisabled;
        Assert.IsTrue(expected.Equals(written), "override must equal the winning record plus InitiallyDisabled");
        Assert.AreEqual(new P3Float(1, 2, 3), written.Placement!.Position, "winning (later plugin) data is preserved");
        Assert.AreEqual(1, written.LinkedReferences.Count);
        Assert.IsFalse(patch.Npcs.Any(), "NPC bases are never modified");
        Assert.IsTrue(patch.EnumerateMajorRecords().All(r => r is IPlacedNpcGetter or ICellGetter or IWorldspaceGetter));
    }

    [TestMethod]
    public void ScriptedOverrideAlsoLosesItsReferenceScriptsAndNothingElse()
    {
        // Specters pattern: a script on the reference would enable it again at night.
        var world = new World();
        var scripted = world.Place(1, p =>
        {
            p.LevelModifier = Level.Hard;
            p.VirtualMachineAdapter = new VirtualMachineAdapter
            {
                Scripts = { new ScriptEntry { Name = "AutomaticLightSwitchScript", Properties = { new ScriptFloatProperty { Name = "LightsOnTime", Data = 22f } } } },
            };
        });

        var (outcome, patch) = Run(world, Settings(0));

        var written = patch.EnumerateMajorRecords<IPlacedNpcGetter>().Single();
        Assert.IsTrue(written.MajorFlags.HasFlag(PlacedNpc.MajorFlag.InitiallyDisabled));
        Assert.IsNull(written.VirtualMachineAdapter, "reference scripts must be removed from a disabled placement");
        var expected = scripted.DeepCopy();
        expected.MajorFlags |= PlacedNpc.MajorFlag.InitiallyDisabled;
        expected.VirtualMachineAdapter = null;
        Assert.IsTrue(expected.Equals(written), "only InitiallyDisabled and the script removal may differ");
        Assert.AreEqual(Level.Hard, written.LevelModifier);
        Assert.AreEqual(1, outcome.Apply!.Plugins.Single().ScriptsRemoved);
        StringAssert.Contains(outcome.Report, "REJECTED with reference scripts .........     1   (AutomaticLightSwitchScript 1)");
        StringAssert.Contains(outcome.Report, "of which reference scripts removed ......     1");
    }

    [TestMethod]
    public void DryRunReportFlagsRejectedScriptedPlacements()
    {
        var world = new World();
        world.PlacePlain(3);
        world.Place(9000, p => p.VirtualMachineAdapter = new VirtualMachineAdapter { Scripts = { new ScriptEntry { Name = "masterambushscript" } } });

        var (outcome, patch) = Run(world, Settings(0, dryRun: true));

        Assert.AreEqual(0, patch.EnumerateMajorRecords().Count());
        StringAssert.Contains(outcome.Report, "REJECTED with reference scripts .........     1   (masterambushscript 1)");
        StringAssert.Contains(outcome.Report, "their scripts would be removed with the disable");
    }

    [TestMethod]
    public void SelectedScriptedPlacementKeepsItsScripts()
    {
        var world = new World();
        world.Place(1, p => p.VirtualMachineAdapter = new VirtualMachineAdapter { Scripts = { new ScriptEntry { Name = "masterambushscript" } } });
        var (outcome, patch) = Run(world, Settings(100));
        Assert.AreEqual(0, patch.EnumerateMajorRecords().Count(), "selected placements get no override at all");
        Assert.IsFalse(outcome.Report.Contains("REJECTED with reference scripts"));
    }

    [TestMethod]
    public void GroupsAreAppliedAtomically()
    {
        var world = new World();
        for (var i = 0; i < 30; i++)
        {
            var trigger = new PlacedObject(world.Creatures) { MajorRecordFlagsRaw = 0x400 };
            world.CreatureCell.Persistent.Add(trigger);
            for (var m = 0; m < 3; m++)
                world.Place(i * 1000 + m, p => p.LinkedReferences.Add(new LinkedReferences { Reference = trigger.ToLink<IPlacedGetter>() }));
        }

        var (outcome, patch) = Run(world, Settings(50));

        var overrides = Overrides(patch);
        var units = outcome.Analyses.Single().Units;
        Assert.AreEqual(30, units.Count(u => u.IsGroup));
        Assert.IsTrue(units.Any(u => u.Decision == SelectionDecision.Rejected) && units.Any(u => u.Decision == SelectionDecision.Selected));
        foreach (var unit in units)
        {
            var disabled = unit.Members.Count(overrides.Contains);
            Assert.AreEqual(unit.Decision == SelectionDecision.Rejected ? unit.Members.Count : 0, disabled, $"group {unit.Index} applied partially");
        }
    }

    [TestMethod]
    public void ZeroPercentDisablesAllEligibleAndHundredCreatesNothing()
    {
        var world = new World();
        world.PlacePlain(25);
        world.Place(9000, p => p.LocationRefTypes = new ExtendedList<IFormLinkGetter<ILocationReferenceTypeGetter>> { world.Boss.ToLink() });

        var (_, zero) = Run(world, Settings(0));
        Assert.AreEqual(25, Overrides(zero).Count, "all eligible, not the excluded one");

        var (hundred, full) = Run(world, Settings(100));
        Assert.AreEqual(0, full.EnumerateMajorRecords().Count());
        Assert.AreEqual(0, hundred.Apply!.TotalPlanned);
    }

    [TestMethod]
    public void SameSeedIsIdenticalAndDifferentSeedDiffers()
    {
        var world = new World();
        world.PlacePlain(60);
        var a = Overrides(Run(world, Settings(50, seed: 1)).Patch);
        var b = Overrides(Run(world, Settings(50, seed: 1)).Patch);
        var c = Overrides(Run(world, Settings(50, seed: 2)).Patch);
        Assert.IsTrue(a.SetEquals(b));
        Assert.IsFalse(a.SetEquals(c));
    }

    [TestMethod]
    public void AlreadyDisabledWinnerGetsNoOverride()
    {
        var world = new World();
        var placed = world.PlacePlain(5);
        world.OverrideInLater(placed[0], p => p.MajorFlags |= PlacedNpc.MajorFlag.InitiallyDisabled);
        var (_, patch) = Run(world, Settings(0), withLater: true);
        Assert.IsFalse(Overrides(patch).Contains(placed[0].FormKey));
        Assert.AreEqual(4, Overrides(patch).Count);
    }

    [TestMethod]
    public void WinningRecordChangedAfterAnalysisFailsWithoutWritingAnything()
    {
        // Analyse without the later plugin, then apply against a load order where it deleted a planned record.
        var world = new World();
        var trigger = new PlacedObject(world.Creatures) { MajorRecordFlagsRaw = 0x400 };
        world.CreatureCell.Persistent.Add(trigger);
        var first = world.Place(1, p => p.LinkedReferences.Add(new LinkedReferences { Reference = trigger.ToLink<IPlacedGetter>() }));
        world.Place(2, p => p.LinkedReferences.Add(new LinkedReferences { Reference = trigger.ToLink<IPlacedGetter>() }));
        world.PlacePlain(10);
        world.OverrideInLater(first, p => p.IsDeleted = true);

        var before = world.LoadOrder();
        var analyses = DryRun.Analyze(Settings(0), before, before.ToImmutableLinkCache());
        var after = world.LoadOrder(withLater: true);
        var patch = new SkyrimMod(ModKey.FromFileName("Synthesis.esp"), SkyrimRelease.SkyrimSE);

        var result = ApplyPhase.Apply(analyses, patch, after.ToImmutableLinkCache());

        Assert.IsFalse(result.Succeeded);
        Assert.AreEqual(0, patch.EnumerateMajorRecords().Count(), "nothing may be written when validation fails");
        var error = result.Errors.Single();
        StringAssert.Contains(error, first.FormKey.ToString());
        StringAssert.Contains(error, "deleted");
        StringAssert.Contains(error, "whole group cannot be applied");
    }

    [TestMethod]
    public void ValidationRejectsDisabledChangedOrMissingRecords()
    {
        var world = new World();
        var placed = world.PlacePlain(3);
        var before = world.LoadOrder();
        var analyses = DryRun.Analyze(Settings(0), before, before.ToImmutableLinkCache());
        var plan = ApplyPhase.Plan(analyses);
        Assert.AreEqual(3, plan.Count);

        world.OverrideInLater(placed[0], p => p.MajorFlags |= PlacedNpc.MajorFlag.InitiallyDisabled);
        world.OverrideInLater(placed[1], p => p.Base = new FormLinkNullable<INpcGetter>(new FormKey(MasterKey, 0x999)));
        world.CreatureCell.Temporary.Remove(placed[2]);
        var errors = ApplyPhase.Validate(plan, world.LoadOrder(withLater: true).ToImmutableLinkCache());

        Assert.AreEqual(3, errors.Count, string.Join("\n", errors));
        Assert.IsTrue(errors.Any(e => e.Contains("already initially disabled")));
        Assert.IsTrue(errors.Any(e => e.Contains("base changed")));
        Assert.IsTrue(errors.Any(e => e.Contains("not found")));
    }

    [TestMethod]
    public void ValidationRejectsScriptsChangedAfterAnalysis()
    {
        var world = new World();
        var placed = world.PlacePlain(1)[0];
        var before = world.LoadOrder();
        var plan = ApplyPhase.Plan(DryRun.Analyze(Settings(0), before, before.ToImmutableLinkCache()));

        world.OverrideInLater(placed, p => p.VirtualMachineAdapter = new VirtualMachineAdapter { Scripts = { new ScriptEntry { Name = "SomeQuestScript" } } });
        var errors = ApplyPhase.Validate(plan, world.LoadOrder(withLater: true).ToImmutableLinkCache());

        StringAssert.Contains(errors.Single(), "reference scripts changed since analysis");
    }

    [TestMethod]
    public void PatcherRunThrowsAfterReportingWhenApplyStops()
    {
        var world = new World();
        world.PlacePlain(3);
        var loadOrder = world.LoadOrder();
        var analyses = DryRun.Analyze(Settings(0), loadOrder, loadOrder.ToImmutableLinkCache());
        var failing = new ApplyPhase.Result([], ["000801:Creatures.esp: winning ACHR record not found in the load order"]);
        var report = DryRunReport.Render(analyses, LoadOrderLayout.FromMods(loadOrder), 38174, false, failing);
        StringAssert.Contains(report, "APPLY STOPPED - nothing was written");
    }

    [TestMethod]
    public void RecordsFromEarlierPatchersInTheGroupAreLeftAlone()
    {
        var world = new World();
        world.PlacePlain(10);
        var loadOrder = world.LoadOrder();
        var patch = new SkyrimMod(ModKey.FromFileName("Synthesis.esp"), SkyrimRelease.SkyrimSE);
        patch.Npcs.Add(new Npc(patch) { EditorID = "FromEarlierPatcher" });

        var outcome = PatcherRun.Execute(Settings(0), loadOrder, loadOrder.ToImmutableLinkCache(), patch);

        Assert.IsTrue(outcome.Apply!.Succeeded);
        Assert.AreEqual(10, Overrides(patch).Count);
        Assert.AreEqual("FromEarlierPatcher", patch.Npcs.Single().EditorID);
    }
}
