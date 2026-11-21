using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Cache;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;

namespace ModActorSpawnRandomizer;

/// <summary>
/// Turns the analysis into minimal ACHR overrides. It consumes the analysis as-is: only placements of
/// units the selection REJECTED are touched, and each override is a copy of the winning record with
/// <c>InitiallyDisabled</c> added. Everything is validated before anything is written, so a run either
/// applies every planned unit completely or writes nothing.
/// </summary>
public static class ApplyPhase
{
    public sealed record PlannedDisable(ModKey Plugin, int UnitIndex, FormKey FormKey, PlacementFacts Facts);

    public sealed record PluginApplyResult(
        ModKey Plugin,
        int PlannedPlacements,
        int PlannedUnits,
        int OverridesWritten,
        int SelectedUntouched,
        int NotApplicableUntouched);

    public sealed record Result(
        IReadOnlyList<PluginApplyResult> Plugins,
        IReadOnlyList<string> Errors)
    {
        public bool Succeeded => Errors.Count == 0;
        public int TotalPlanned => Plugins.Sum(p => p.PlannedPlacements);
        public int TotalWritten => Plugins.Sum(p => p.OverridesWritten);
    }

    /// <summary>Every member of every REJECTED unit, in a stable order.</summary>
    public static IReadOnlyList<PlannedDisable> Plan(IEnumerable<PluginAnalysis> analyses)
    {
        var plan = new List<PlannedDisable>();
        foreach (var analysis in analyses.Where(a => a.Found))
        {
            var facts = analysis.Placements.ToDictionary(p => p.Facts.FormKey, p => p.Facts);
            foreach (var unit in analysis.Units.Where(u => u.Decision == SelectionDecision.Rejected))
            {
                if (unit.Status is not (PlacementStatus.Eligible or PlacementStatus.Grouped))
                    throw new InvalidOperationException($"Unit {unit.Index} of {analysis.Plugin} is rejected but has status {unit.Status}.");
                foreach (var member in unit.Members)
                    plan.Add(new PlannedDisable(analysis.Plugin, unit.Index, member, facts[member]));
            }
        }

        return plan;
    }

    /// <summary>
    /// Re-checks every planned record against the current winning load order. Returns actionable errors;
    /// an empty list means every planned unit can be applied completely.
    /// </summary>
    public static IReadOnlyList<string> Validate(
        IReadOnlyList<PlannedDisable> plan,
        ILinkCache<ISkyrimMod, ISkyrimModGetter> linkCache)
    {
        var errors = new List<string>();
        foreach (var unit in plan.GroupBy(p => (p.Plugin, p.UnitIndex)))
        {
            foreach (var planned in unit)
            {
                var problem = CheckWinning(planned, linkCache);
                if (problem is not null)
                {
                    var group = unit.Count() > 1 ? $" (member of a {unit.Count()}-placement group; the whole group cannot be applied)" : "";
                    errors.Add($"{planned.FormKey}: {problem}{group}");
                }
            }
        }

        return errors;
    }

    private static string? CheckWinning(PlannedDisable planned, ILinkCache<ISkyrimMod, ISkyrimModGetter> linkCache)
    {
        if (!linkCache.TryResolveContext<IPlacedNpc, IPlacedNpcGetter>(planned.FormKey, out var context))
            return "winning ACHR record not found in the load order";

        var record = context.Record;
        if (record.FormKey.ModKey != planned.Plugin)
            return $"unexpected origin plugin {record.FormKey.ModKey}";
        if (record.IsDeleted)
            return $"winning record (from {context.ModKey}) is deleted; deleted references are never resurrected";
        if (record.MajorFlags.HasFlag(PlacedNpc.MajorFlag.InitiallyDisabled))
            return $"winning record (from {context.ModKey}) is already initially disabled, but was analysed as active";
        if (record.Base.FormKey != planned.Facts.BaseFormKey)
            return $"base changed from {planned.Facts.BaseFormKey} to {record.Base.FormKey} (winning record from {context.ModKey})";
        if (record.EnableParent is not null && !record.EnableParent.Reference.IsNull)
            return $"winning record (from {context.ModKey}) has an enable parent; enable-parented placements are not applied";
        if (context.ModKey != planned.Facts.WinningPlugin)
            return $"winning record now comes from {context.ModKey}, analysis used {planned.Facts.WinningPlugin}";
        if (FindCell(context) is null)
            return "winning record has no containing cell";
        return null;
    }

    /// <summary>
    /// Validates the whole plan and, only if everything is valid, writes one override per planned placement
    /// into <paramref name="patch"/>. Post-write invariants are checked; violations are reported as errors.
    /// </summary>
    public static Result Apply(
        IReadOnlyList<PluginAnalysis> analyses,
        ISkyrimMod patch,
        ILinkCache<ISkyrimMod, ISkyrimModGetter> linkCache)
    {
        var plan = Plan(analyses);
        var errors = Validate(plan, linkCache).ToList();
        if (errors.Count > 0)
            return new Result(Summarize(analyses, plan, written: new HashSet<FormKey>()), errors);

        // Synthesis chains patchers in a group onto one patch: earlier patchers' records are not ours to check.
        var preexisting = RecordKeys(patch);

        var expected = new Dictionary<FormKey, PlacedNpc>();
        foreach (var planned in plan)
        {
            var context = linkCache.ResolveContext<IPlacedNpc, IPlacedNpcGetter>(planned.FormKey);
            var copy = context.Record.DeepCopy();
            copy.MajorFlags |= PlacedNpc.MajorFlag.InitiallyDisabled;
            expected[planned.FormKey] = copy;

            var overrideRecord = context.GetOrAddAsOverride(patch);
            overrideRecord.MajorFlags |= PlacedNpc.MajorFlag.InitiallyDisabled;
        }

        errors.AddRange(VerifyPatch(patch, expected, preexisting));
        var written = patch.EnumerateMajorRecords<IPlacedNpcGetter>()
            .Select(r => r.FormKey)
            .Where(expected.ContainsKey)
            .ToHashSet();
        return new Result(Summarize(analyses, plan, written), errors);
    }

    public static HashSet<FormKey> RecordKeys(ISkyrimModGetter mod) =>
        mod.EnumerateMajorRecords().Select(r => r.FormKey).ToHashSet();

    /// <summary>
    /// Records this run added to the patch may only be the planned ACHR overrides (each equal to its winning
    /// record plus InitiallyDisabled) and the cell/worldspace records Mutagen adds as their containers.
    /// Records that were already in the patch before the run (earlier patchers in the group) are ignored.
    /// </summary>
    public static IReadOnlyList<string> VerifyPatch(
        ISkyrimModGetter patch,
        IReadOnlyDictionary<FormKey, PlacedNpc> expected,
        IReadOnlySet<FormKey> preexisting)
    {
        var errors = new List<string>();
        var placed = patch.EnumerateMajorRecords<IPlacedNpcGetter>()
            .Where(r => expected.ContainsKey(r.FormKey) || !preexisting.Contains(r.FormKey))
            .ToList();

        foreach (var record in placed)
        {
            if (!expected.TryGetValue(record.FormKey, out var want))
                errors.Add($"{record.FormKey}: unplanned ACHR override in the patch");
            else if (!want.Equals(record))
                errors.Add($"{record.FormKey}: override differs from the winning record by more than InitiallyDisabled");
        }

        foreach (var missing in expected.Keys.Except(placed.Select(r => r.FormKey)))
            errors.Add($"{missing}: planned override missing from the patch");

        foreach (var other in patch.EnumerateMajorRecords().Where(r => !preexisting.Contains(r.FormKey)))
        {
            if (other is IPlacedNpcGetter or ICellGetter or IWorldspaceGetter) continue;
            errors.Add($"{other.FormKey}: unexpected {other.GetType().Name} record added to the patch");
        }

        return errors;
    }

    private static IReadOnlyList<PluginApplyResult> Summarize(
        IReadOnlyList<PluginAnalysis> analyses,
        IReadOnlyList<PlannedDisable> plan,
        IReadOnlySet<FormKey> written)
    {
        return analyses.Where(a => a.Found).Select(a =>
        {
            var planned = plan.Where(p => p.Plugin == a.Plugin).ToList();
            return new PluginApplyResult(
                a.Plugin,
                planned.Count,
                planned.Select(p => p.UnitIndex).Distinct().Count(),
                planned.Count(p => written.Contains(p.FormKey)),
                a.Units.Where(u => u.Decision == SelectionDecision.Selected).Sum(u => u.Members.Count),
                a.Units.Where(u => u.Decision == SelectionDecision.NotApplicable).Sum(u => u.Members.Count));
        }).ToList();
    }

    private static ICellGetter? FindCell(IModContext<ISkyrimMod, ISkyrimModGetter, IPlacedNpc, IPlacedNpcGetter> context)
    {
        var node = context.Parent;
        while (node is not null && node.Record is not ICellGetter) node = node.Parent;
        return node?.Record as ICellGetter;
    }
}
