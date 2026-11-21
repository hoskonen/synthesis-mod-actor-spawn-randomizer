using Mutagen.Bethesda.Plugins.Cache;
using Mutagen.Bethesda.Skyrim;

namespace ModActorSpawnRandomizer;

/// <summary>
/// One run of the patcher: analyse once, then either only report (DryRun) or apply the same analysis.
/// The caller prints <see cref="Outcome.Report"/> and must fail the run when <see cref="Outcome.Failure"/>
/// is set; in that case nothing was written by the apply phase.
/// </summary>
public static class PatcherRun
{
    public sealed record Outcome(
        IReadOnlyList<PluginAnalysis> Analyses,
        ApplyPhase.Result? Apply,
        string Report,
        string? Failure);

    public static Outcome Execute(
        Settings settings,
        IReadOnlyList<ISkyrimModGetter> activeLoadOrder,
        ILinkCache<ISkyrimMod, ISkyrimModGetter> linkCache,
        ISkyrimMod patch)
    {
        var analyses = DryRun.Analyze(settings, activeLoadOrder, linkCache);
        var recordsBefore = ApplyPhase.RecordKeys(patch);

        ApplyPhase.Result? apply = settings.DryRun ? null : ApplyPhase.Apply(analyses, patch, linkCache);

        var report = DryRunReport.Render(
            analyses,
            LoadOrderLayout.FromMods(activeLoadOrder),
            settings.Seed,
            settings.DetailedReport,
            apply);

        string? failure = null;
        if (settings.DryRun && !ApplyPhase.RecordKeys(patch).SetEquals(recordsBefore))
            failure = "Read-only invariant violated: the dry run changed the output patch.";
        else if (apply is { Succeeded: false })
            failure = "Apply phase stopped; see the report above." + Environment.NewLine + string.Join(Environment.NewLine, apply.Errors);
        else if (apply is not null && apply.TotalWritten != apply.TotalPlanned)
            failure = $"Apply phase wrote {apply.TotalWritten} overrides but planned {apply.TotalPlanned}.";

        return new Outcome(analyses, apply, report, failure);
    }
}
