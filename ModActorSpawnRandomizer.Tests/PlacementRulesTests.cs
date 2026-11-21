using Microsoft.VisualStudio.TestTools.UnitTesting;
using Mutagen.Bethesda.Plugins;
using static ModActorSpawnRandomizer.Tests.Fixtures;

namespace ModActorSpawnRandomizer.Tests;

[TestClass]
public sealed class PlacementRulesTests
{
    private static PlacementStatus StatusOf(PlacementFacts f) => PlacementRules.StatusOf(PlacementRules.Evaluate(f));

    [TestMethod]
    public void PlainPlacementIsEligible()
    {
        Assert.AreEqual(0, PlacementRules.Evaluate(Plain(0x801)).Count);
        Assert.AreEqual(PlacementStatus.Eligible, StatusOf(Plain(0x801)));
    }

    [TestMethod]
    public void OutgoingLinksAndSafeScriptsStayEligible()
    {
        // Mourning Souls / Specters pattern: links and scripts that only point outward.
        var facts = Plain(0x801) with
        {
            LinkedReferenceTargets = [Ref(0x900)],
            ActivateParentTargets = [Ref(0x901)],
            Scripts = [new ScriptFacts("masterambushscript", null), new ScriptFacts("AutomaticLightSwitchScript", null)],
        };
        Assert.AreEqual(PlacementStatus.Eligible, StatusOf(facts));
    }

    [DataTestMethod]
    [DataRow(PlacementReason.Deleted)]
    [DataRow(PlacementReason.AlreadyDisabled)]
    [DataRow(PlacementReason.UniqueOrEssentialBase)]
    [DataRow(PlacementReason.InboundReference)]
    [DataRow(PlacementReason.QuestScript)]
    [DataRow(PlacementReason.DeathCounterScript)]
    [DataRow(PlacementReason.BossLocationRefType)]
    [DataRow(PlacementReason.OppositeEnableParent)]
    public void ExclusionRulesExclude(PlacementReason expected)
    {
        var facts = expected switch
        {
            PlacementReason.Deleted => Plain(0x801) with { Deleted = true },
            PlacementReason.AlreadyDisabled => Plain(0x801) with { InitiallyDisabled = true },
            PlacementReason.UniqueOrEssentialBase => Plain(0x801) with { BaseEssential = true },
            PlacementReason.InboundReference => Plain(0x801) with { InboundReferences = [new InboundReference(Ref(0x950), "Quest", "SomeQuest")] },
            PlacementReason.QuestScript => Plain(0x801) with { Scripts = [new ScriptFacts("C05SilverHandScript", "C05")] },
            PlacementReason.DeathCounterScript => Plain(0x801) with { Scripts = [new ScriptFacts("defaultCounterIncrementOnDeath", null)] },
            PlacementReason.BossLocationRefType => Plain(0x801) with { LocationRefTypes = ["Boss"] },
            PlacementReason.OppositeEnableParent => Plain(0x801) with { EnableParent = new EnableParentFacts(Ref(0x900), true, "XMarker") },
            _ => throw new ArgumentOutOfRangeException(nameof(expected)),
        };

        CollectionAssert.Contains(PlacementRules.Evaluate(facts).ToList(), expected);
        Assert.AreEqual(PlacementStatus.Excluded, StatusOf(facts));
    }

    [DataTestMethod]
    [DataRow(PlacementReason.EnableParentUnverified)]
    [DataRow(PlacementReason.UnknownScript)]
    [DataRow(PlacementReason.UnknownLocationRefType)]
    [DataRow(PlacementReason.PersistentReference)]
    [DataRow(PlacementReason.PersistentLocation)]
    [DataRow(PlacementReason.MerchantContainer)]
    [DataRow(PlacementReason.UnresolvedBase)]
    [DataRow(PlacementReason.MissingPosition)]
    public void UnknownStructuresNeedInvestigation(PlacementReason expected)
    {
        var facts = expected switch
        {
            PlacementReason.EnableParentUnverified => Plain(0x801) with { EnableParent = new EnableParentFacts(Ref(0x900), false, "XMarker") },
            PlacementReason.UnknownScript => Plain(0x801) with { Scripts = [new ScriptFacts("SomeModScript", null)] },
            PlacementReason.UnknownLocationRefType => Plain(0x801) with { LocationRefTypes = ["CWFortMonster"] },
            PlacementReason.PersistentReference => Plain(0x801) with { Persistent = true },
            PlacementReason.PersistentLocation => Plain(0x801) with { HasPersistentLocation = true },
            PlacementReason.MerchantContainer => Plain(0x801) with { HasMerchantContainer = true },
            PlacementReason.UnresolvedBase => Plain(0x801) with { BaseResolved = false },
            PlacementReason.MissingPosition => Plain(0x801) with { Position = null },
            _ => throw new ArgumentOutOfRangeException(nameof(expected)),
        };

        CollectionAssert.Contains(PlacementRules.Evaluate(facts).ToList(), expected);
        Assert.AreEqual(PlacementStatus.NeedsInvestigation, StatusOf(facts));
    }

    [TestMethod]
    public void ExclusionOutranksInvestigation()
    {
        var facts = Plain(0x801) with
        {
            EnableParent = new EnableParentFacts(Ref(0x900), false, "XMarker"),
            LocationRefTypes = ["Boss"],
        };
        Assert.AreEqual(PlacementStatus.Excluded, StatusOf(facts));
    }

    [TestMethod]
    public void QuestOwnedScriptIsExcludedEvenIfScriptNameIsOnSafeList()
    {
        var facts = Plain(0x801) with { Scripts = [new ScriptFacts("masterambushscript", "SomeQuest")] };
        Assert.AreEqual(PlacementStatus.Excluded, StatusOf(facts));
    }

    [TestMethod]
    public void EveryReasonHasADefinedSeverity()
    {
        foreach (var reason in Enum.GetValues<PlacementReason>())
            Assert.IsTrue(Enum.IsDefined(PlacementRules.SeverityOf(reason)));
    }
}
