using Microsoft.VisualStudio.TestTools.UnitTesting;
using Mutagen.Bethesda.Plugins;
using static ModActorSpawnRandomizer.Tests.Fixtures;

namespace ModActorSpawnRandomizer.Tests;

[TestClass]
public sealed class DryRunReportTests
{
    private static readonly LoadOrderLayout Layout = new([(Skyrim, false), (Plugin, true)]);

    private static string Render(IReadOnlyList<PlacementFacts> placements, bool detailed = false) =>
        DryRunReport.Render([PluginAnalyzer.Analyze(Plugin, placements, 0, 50, 38174)], Layout, 38174, detailed);

    [TestMethod]
    public void StatesReadOnlyAndSeparatesEligibilityFromSelection()
    {
        var report = Render([Plain(0x801)]);
        StringAssert.Contains(report, "DRY RUN (read-only");
        StringAssert.Contains(report, "Eligibility (safety rules; independent of seed)");
        StringAssert.Contains(report, "Selection (simulated; seed 38174");
    }

    [TestMethod]
    public void GroupHeaderShowsEnableParentWithInGameFormId()
    {
        var ep = new EnableParentFacts(Ref(0xB01), false, "XMarker (persistent, initially disabled)");
        var report = Render([Plain(0xAFB) with { EnableParent = ep }, Plain(0xAFC) with { EnableParent = ep }]);
        StringAssert.Contains(report, "enable parent  FE000B01 000B01:Creatures.esp XMarker (persistent, initially disabled)  x2");
        StringAssert.Contains(report, "FE000AFB");
    }

    [TestMethod]
    public void GroupHeaderShowsSharedLinkTarget()
    {
        var trigger = new FormKey(Skyrim, 0x0C14B4);
        var descriptions = new Dictionary<FormKey, string> { [trigger] = "defaultActivateSelfTRIG (persistent)" };
        var report = Render([
            Plain(0x801) with { ActivateParentTargets = [trigger], TargetDescriptions = descriptions },
            Plain(0x802) with { ActivateParentTargets = [trigger], TargetDescriptions = descriptions }]);
        StringAssert.Contains(report, "shared link    000C14B4 0C14B4:Skyrim.esm defaultActivateSelfTRIG (persistent)  x2");
    }

    [TestMethod]
    public void MultiCellGroupIsFlaggedAsLocationSwitch()
    {
        var ep = new EnableParentFacts(new FormKey(Skyrim, 0x04658A), false, "XMarker (persistent)");
        var otherCell = new CellInfo(new FormKey(Skyrim, 0x1234), "FortExterior", false, "Tamriel", 1, 2);
        var report = Render([
            Plain(0x801) with { EnableParent = ep },
            Plain(0x802) with { EnableParent = ep, Cell = otherCell }]);
        StringAssert.Contains(report, "spans 2 cells (location switch, not a single encounter)");
    }

    [TestMethod]
    public void DetailedReportListsEveryPlacementWithScore()
    {
        var report = Render([Plain(0x801), Plain(0x802) with { LocationRefTypes = ["Boss"] }], detailed: true);
        StringAssert.Contains(report, "All placements (detailed)");
        StringAssert.Contains(report, "score=0x");
        StringAssert.Contains(report, "BossLocationRefType");
    }
}
