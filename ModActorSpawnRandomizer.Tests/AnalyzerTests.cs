using Microsoft.VisualStudio.TestTools.UnitTesting;
using Mutagen.Bethesda.Plugins;
using static ModActorSpawnRandomizer.Tests.Fixtures;

namespace ModActorSpawnRandomizer.Tests;

[TestClass]
public sealed class EncounterGroupingTests
{
    [TestMethod]
    public void SharedLinkTargetFormsGroup()
    {
        // Mourning Souls: two creatures woken by the same trigger.
        var a = Plain(0x801) with { ActivateParentTargets = [Ref(0x900)] };
        var b = Plain(0x802) with { ActivateParentTargets = [Ref(0x900)] };
        var c = Plain(0x803) with { ActivateParentTargets = [Ref(0x901)] };
        var groups = EncounterGrouping.Build([a, b, c]);
        Assert.AreEqual(2, groups.Count);
        var pair = groups.Single(g => g.Members.Count == 2);
        Assert.AreEqual(GroupKinds.SharedLinkTarget, pair.Kinds);
        Assert.AreEqual(a.FormKey, EncounterGrouping.UnitKey(pair));
    }

    [TestMethod]
    public void SharedEnableParentFormsGroup()
    {
        var ep = new EnableParentFacts(Ref(0xB01), false, "XMarker");
        var groups = EncounterGrouping.Build([
            Plain(0xAFB) with { EnableParent = ep },
            Plain(0xAFC) with { EnableParent = ep },
            Plain(0xAFD) with { EnableParent = ep }]);
        Assert.AreEqual(1, groups.Count);
        Assert.AreEqual(GroupKinds.SharedEnableParent, groups[0].Kinds);
    }

    [TestMethod]
    public void ExactPositionInSameCellFormsGroupButNearbyDoesNot()
    {
        // Specters: specter and skeleton at exactly the same position.
        var specter = Plain(0x990) with { Position = new Position(10f, 20f, 30f) };
        var skeleton = Plain(0x8AE) with { Position = new Position(10f, 20f, 30f) };
        var nearby = Plain(0x991) with { Position = new Position(10.001f, 20f, 30f) };
        var otherCell = Plain(0x992) with
        {
            Position = new Position(10f, 20f, 30f),
            Cell = new CellInfo(new FormKey(Skyrim, 0x1234), "Other", true, null, null, null),
        };
        var groups = EncounterGrouping.Build([specter, skeleton, nearby, otherCell]);
        var pair = groups.Single(g => g.Members.Count == 2);
        Assert.AreEqual(GroupKinds.ExactPosition, pair.Kinds);
        Assert.AreEqual(skeleton.FormKey, EncounterGrouping.UnitKey(pair), "unit key is the lowest member FormID");
    }

    [TestMethod]
    public void GroupsAreTransitiveAndIndependentOfInputOrder()
    {
        var a = Plain(0x801) with { LinkedReferenceTargets = [Ref(0x900)] };
        var b = Plain(0x802) with { LinkedReferenceTargets = [Ref(0x900), Ref(0x901)] };
        var c = Plain(0x803) with { LinkedReferenceTargets = [Ref(0x901)] };
        var forward = EncounterGrouping.Build([a, b, c]);
        var reversed = EncounterGrouping.Build([c, b, a]);
        Assert.AreEqual(1, forward.Count);
        CollectionAssert.AreEqual(
            forward[0].Members.Select(m => m.FormKey).ToList(),
            reversed[0].Members.Select(m => m.FormKey).ToList());
    }
}

[TestClass]
public sealed class PluginAnalyzerTests
{
    private static PluginAnalysis Analyze(IReadOnlyList<PlacementFacts> placements, double probability = 50, int seed = 38174, int overrides = 0) =>
        PluginAnalyzer.Analyze(Plugin, placements, overrides, probability, seed);

    private static List<PlacementFacts> Plains(int count) =>
        Enumerable.Range(0, count).Select(i => Plain((uint)(0x800 + i))).ToList();

    [TestMethod]
    public void OnlyEligibleUnitsAreSelectedOrRejected()
    {
        var placements = Plains(40);
        placements[0] = placements[0] with { LocationRefTypes = ["Boss"] };
        placements[1] = placements[1] with { Persistent = true };
        var a = Analyze(placements);

        Assert.AreEqual(SelectionDecision.NotApplicable, a.Units.Single(u => u.Key == placements[0].FormKey).Decision);
        Assert.AreEqual(SelectionDecision.NotApplicable, a.Units.Single(u => u.Key == placements[1].FormKey).Decision);
        Assert.IsTrue(a.Units.Where(u => u.Status == PlacementStatus.Eligible).All(u => u.Decision != SelectionDecision.NotApplicable));
        Assert.IsTrue(a.Units.Where(u => u.Decision != SelectionDecision.NotApplicable).All(u => u.Score.HasValue));
    }

    [TestMethod]
    public void SeedChangesSelectionButNeverEligibility()
    {
        var placements = Plains(60);
        placements[3] = placements[3] with { EnableParent = new EnableParentFacts(Ref(0xB01), false, "XMarker") };
        placements[7] = placements[7] with { Scripts = [new ScriptFacts("C05SilverHandScript", "C05")] };

        var one = Analyze(placements, seed: 1);
        var two = Analyze(placements, seed: 2);

        CollectionAssert.AreEqual(one.Placements.Select(p => p.Status).ToList(), two.Placements.Select(p => p.Status).ToList());
        CollectionAssert.AreNotEqual(one.Units.Select(u => u.Decision).ToList(), two.Units.Select(u => u.Decision).ToList());
    }

    [TestMethod]
    public void SameInputInAnyOrderGivesIdenticalAnalysis()
    {
        var placements = Plains(50);
        placements[4] = placements[4] with { LinkedReferenceTargets = [Ref(0x990)] };
        placements[9] = placements[9] with { LinkedReferenceTargets = [Ref(0x990)] };

        var forward = Analyze(placements);
        var reversed = Analyze(placements.AsEnumerable().Reverse().ToList());

        CollectionAssert.AreEqual(
            forward.Placements.Select(p => (p.Facts.FormKey, p.Status, forward.Units[p.UnitIndex].Decision)).ToList(),
            reversed.Placements.Select(p => (p.Facts.FormKey, p.Status, reversed.Units[p.UnitIndex].Decision)).ToList());
    }

    [TestMethod]
    public void UnresolvedMemberMakesWholeGroupNeedInvestigation()
    {
        // HPE ambush: members share an enable parent; none may be touched until validated in game.
        var ep = new EnableParentFacts(Ref(0xB01), false, "XMarker");
        var placements = new List<PlacementFacts>
        {
            Plain(0xAFB) with { EnableParent = ep, LinkedReferenceTargets = [Ref(0xB01)] },
            Plain(0xAFC) with { EnableParent = ep, LinkedReferenceTargets = [Ref(0xB01)] },
            Plain(0xAF5),
        };
        var a = Analyze(placements);
        var group = a.Units.Single(u => u.IsGroup);
        Assert.AreEqual(PlacementStatus.NeedsInvestigation, group.Status);
        Assert.AreEqual(SelectionDecision.NotApplicable, group.Decision);
        Assert.AreEqual(PlacementStatus.Eligible, a.Placements.Single(p => p.Facts.FormKey.ID == 0xAF5).Status);
    }

    [TestMethod]
    public void ExcludedMemberExcludesOtherwiseEligibleGroupMembers()
    {
        var placements = new List<PlacementFacts>
        {
            Plain(0x801) with { LinkedReferenceTargets = [Ref(0x900)], LocationRefTypes = ["Boss"] },
            Plain(0x802) with { LinkedReferenceTargets = [Ref(0x900)] },
        };
        var a = Analyze(placements);
        var follower = a.Placements.Single(p => p.Facts.FormKey.ID == 0x802);
        Assert.AreEqual(PlacementStatus.Excluded, follower.Status);
        CollectionAssert.Contains(follower.Reasons.ToList(), PlacementReason.GroupMemberExcluded);
    }

    [TestMethod]
    public void GroupIsDecidedAsOneUnit()
    {
        var placements = Enumerable.Range(0, 30)
            .SelectMany(i => new[]
            {
                Plain((uint)(0x800 + i * 2)) with { LinkedReferenceTargets = [Ref((uint)(0x900 + i))] },
                Plain((uint)(0x801 + i * 2)) with { LinkedReferenceTargets = [Ref((uint)(0x900 + i))] },
            })
            .ToList();
        var a = Analyze(placements);
        Assert.AreEqual(30, a.Units.Count);
        foreach (var unit in a.Units)
        {
            Assert.AreEqual(PlacementStatus.Grouped, unit.Status);
            Assert.AreEqual(DeterministicSelector.IsSelected(unit.Key, 38174, 50) ? SelectionDecision.Selected : SelectionDecision.Rejected, unit.Decision);
        }
    }

    [TestMethod]
    public void LayoutIsDiagnosticAndDoesNotRejectEligiblePlacements()
    {
        // HPE-like: some groups needing investigation, most placements plain and still eligible.
        var ep = new EnableParentFacts(Ref(0xB01), false, "XMarker");
        var placements = Plains(40);
        placements[0] = placements[0] with { EnableParent = ep };
        placements[1] = placements[1] with { EnableParent = ep };
        placements[2] = placements[2] with { LocationRefTypes = ["Boss"] };
        var a = Analyze(placements, overrides: 3);

        Assert.AreEqual(PluginLayout.Grouped, a.Layout);
        Assert.IsTrue(a.HasWarnings);
        Assert.AreEqual(37, a.Placements.Count(p => p.Status == PlacementStatus.Eligible));
    }

    [TestMethod]
    public void LayoutClassification()
    {
        Assert.AreEqual(PluginLayout.Simple, Analyze(Plains(10)).Layout);

        var linked = Plains(10);
        linked[0] = linked[0] with { LinkedReferenceTargets = [Ref(0x900)] };
        Assert.AreEqual(PluginLayout.SimpleWithLinks, Analyze(linked).Layout);

        Assert.AreEqual(PluginLayout.ProbablyNotCreatureMod, Analyze([]).Layout);

        var questy = Plains(4).Select(p => p with { InboundReferences = [new InboundReference(Ref(0x990), "Quest", "Q")] }).ToList();
        Assert.AreEqual(PluginLayout.ProbablyNotCreatureMod, Analyze(questy).Layout);

        Assert.AreEqual(PluginLayout.ProbablyNotCreatureMod, Analyze(Plains(2), overrides: 5).Layout);
    }

    [TestMethod]
    public void SimplePluginHasNoWarnings()
    {
        var a = Analyze(Plains(50));
        Assert.IsFalse(a.HasWarnings);
        Assert.AreEqual(50, a.Units.Count(u => u.Decision != SelectionDecision.NotApplicable));
    }

    [TestMethod]
    public void FingerprintChangesWhenPlacementsChange()
    {
        var a = PluginAnalyzer.Fingerprint(Plains(5));
        Assert.AreEqual(a, PluginAnalyzer.Fingerprint(Plains(5).AsEnumerable().Reverse()));
        Assert.AreNotEqual(a, PluginAnalyzer.Fingerprint(Plains(6)));
    }
}

[TestClass]
public sealed class LoadOrderLayoutTests
{
    [TestMethod]
    public void FormatsLightAndFullPluginIds()
    {
        var layout = new LoadOrderLayout([
            (ModKey.FromFileName("Skyrim.esm"), false),
            (ModKey.FromFileName("Update.esm"), false),
            (ModKey.FromFileName("ccA.esl"), true),
            (ModKey.FromFileName("Full.esp"), false),
            (ModKey.FromFileName("Light.esp"), true),
        ]);

        Assert.AreEqual("0000ABCD", layout.InGameFormId(new FormKey(ModKey.FromFileName("Skyrim.esm"), 0x00ABCD)));
        Assert.AreEqual("02000AFB", layout.InGameFormId(new FormKey(ModKey.FromFileName("Full.esp"), 0x000AFB)));
        Assert.AreEqual("FE001AFB", layout.InGameFormId(new FormKey(ModKey.FromFileName("Light.esp"), 0x000AFB)));
        Assert.AreEqual("FE:001", layout.Slot(ModKey.FromFileName("Light.esp")));
        Assert.AreEqual("??000AFB", layout.InGameFormId(new FormKey(ModKey.FromFileName("Missing.esp"), 0x000AFB)));
    }
}

[TestClass]
public sealed class SettingsValidatorTests
{
    [TestMethod]
    public void ValidSettingsPass()
    {
        var settings = new Settings { Plugins = [new PluginSelection { Plugin = ModKey.FromFileName("A.esp"), Probability = 30 }] };
        Assert.AreEqual(0, SettingsValidator.Validate(settings).Count);
    }

    [TestMethod]
    public void InvalidSettingsAreReported()
    {
        var settings = new Settings
        {
            Plugins =
            [
                new PluginSelection { Plugin = ModKey.FromFileName("A.esp"), Probability = 101 },
                new PluginSelection { Plugin = ModKey.FromFileName("A.esp"), Probability = double.NaN },
                new PluginSelection(),
            ],
        };
        var errors = SettingsValidator.Validate(settings);
        Assert.AreEqual(4, errors.Count, string.Join("\n", errors));
        Assert.AreEqual(1, SettingsValidator.Validate(new Settings()).Count);
    }
}
