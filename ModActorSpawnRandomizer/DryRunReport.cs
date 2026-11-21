using System.Globalization;
using System.Text;

namespace ModActorSpawnRandomizer;

/// <summary>Human-readable dry-run report. Pure formatting; no analysis happens here.</summary>
public static class DryRunReport
{
    public static string Render(IReadOnlyList<PluginAnalysis> analyses, LoadOrderLayout layout, int seed, bool detailed)
    {
        var sb = new StringBuilder();
        var rule = new string('=', 100);
        sb.AppendLine(rule);
        sb.AppendLine("Mod Actor Spawn Randomizer - DRY RUN (read-only: no records are written)");
        sb.AppendLine($"Algorithm {DeterministicSelector.Domain} | Seed {seed} | {analyses.Count} plugin(s) | detailed report: {(detailed ? "on" : "off")}");
        sb.AppendLine("Eligibility comes from the safety rules and never depends on the seed.");
        sb.AppendLine("Selection is simulated over eligible units only: REJECTED = would be disabled by the future apply phase.");
        sb.AppendLine("IDs: in-game FormID (xEdit / console) followed by the load-order independent FormKey.");
        sb.AppendLine(rule);

        foreach (var analysis in analyses)
        {
            sb.AppendLine();
            RenderPlugin(sb, analysis, layout, detailed);
        }

        return sb.ToString();
    }

    private static void RenderPlugin(StringBuilder sb, PluginAnalysis a, LoadOrderLayout layout, bool detailed)
    {
        sb.AppendLine($"== {a.Plugin}  [{layout.Slot(a.Plugin)}]  probability {Pct(a.Probability)}");
        if (!a.Found)
        {
            foreach (var w in a.Warnings) sb.AppendLine($"  ! {w}");
            return;
        }

        var p = a.Placements;
        var interior = p.Count(x => x.Facts.Cell.Interior);
        sb.AppendLine($"Layout: {LayoutName(a.Layout)}{(a.HasWarnings ? " + WARNINGS" : "")}    fingerprint {a.Fingerprint}");
        sb.AppendLine($"  Placed NPCs created by this plugin ...... {p.Count,5}   (interior {interior} / exterior {p.Count - interior})");
        sb.AppendLine($"  Overrides of other plugins' placements .. {a.OverridesOfOtherPlacements,5}   (never touched)");

        sb.AppendLine();
        sb.AppendLine("Eligibility (safety rules; independent of seed)");
        var groupedUnits = a.Units.Count(u => u.Status == PlacementStatus.Grouped);
        sb.AppendLine($"  Eligible (single) ....................... {Count(p, PlacementStatus.Eligible),5}");
        sb.AppendLine($"  Grouped (eligible as a group) ........... {Count(p, PlacementStatus.Grouped),5}   in {groupedUnits} groups");
        sb.AppendLine($"  Needs investigation (untouched) ......... {Count(p, PlacementStatus.NeedsInvestigation),5}");
        sb.AppendLine($"  Excluded (untouched) .................... {Count(p, PlacementStatus.Excluded),5}");

        sb.AppendLine();
        sb.AppendLine($"Selection (simulated; seed {a.Seed}, keep {Pct(a.Probability)} of eligible units)");
        foreach (var decision in new[] { SelectionDecision.Selected, SelectionDecision.Rejected })
        {
            var units = a.Units.Where(u => u.Decision == decision).ToList();
            var label = decision == SelectionDecision.Selected ? "SELECTED (kept active) ..................." : "REJECTED (would be disabled) .............";
            sb.AppendLine($"  {label} {units.Sum(u => u.Members.Count),5}   placements in {units.Count} units");
        }
        sb.AppendLine($"  Not applicable (unchanged) .............. {a.Units.Where(u => u.Decision == SelectionDecision.NotApplicable).Sum(u => u.Members.Count),5}");

        var reasons = p.SelectMany(x => x.Reasons).GroupBy(r => r).OrderBy(g => PlacementRules.SeverityOf(g.Key)).ThenByDescending(g => g.Count()).ToList();
        if (reasons.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Reasons (a placement can have several)");
            foreach (var g in reasons)
                sb.AppendLine($"  {(PlacementRules.SeverityOf(g.Key) == ReasonSeverity.Exclusion ? "[excluded]     " : "[investigation]")} {g.Key,-32} {g.Count(),5}");
        }

        var groups = a.Units.Where(u => u.IsGroup).ToList();
        if (groups.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine($"Encounter groups: {groups.Count}   ({string.Join(", ", KindCounts(groups))})");
            sb.AppendLine($"  sizes: {string.Join(" ", groups.GroupBy(g => g.Members.Count).OrderBy(g => g.Key).Select(g => $"{g.Key}x{g.Count()}"))}");
            foreach (var status in new[] { PlacementStatus.Grouped, PlacementStatus.NeedsInvestigation, PlacementStatus.Excluded })
            {
                var n = groups.Count(g => g.Status == status);
                if (n > 0) sb.AppendLine($"  {StatusName(status),-22} {n,4} groups");
            }
        }

        if (a.Warnings.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Warnings (manual review)");
            foreach (var w in a.Warnings) sb.AppendLine($"  ! {w}");
        }

        if (a.Notes.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Notes");
            foreach (var n in a.Notes) sb.AppendLine($"  - {n}");
        }

        var byKey = p.ToDictionary(x => x.Facts.FormKey);

        var notSelectable = a.Units.Where(u => u.Decision == SelectionDecision.NotApplicable).ToList();
        if (notSelectable.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Untouched placements (excluded or needing investigation)");
            foreach (var unit in notSelectable.OrderBy(u => u.Status).ThenBy(u => u.Key.ID))
                RenderUnit(sb, unit, byKey, layout, showDecision: false);
        }

        var selectableGroups = groups.Where(g => g.Decision != SelectionDecision.NotApplicable).ToList();
        if (selectableGroups.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Selectable encounter groups (whole group kept or disabled together)");
            foreach (var unit in selectableGroups)
                RenderUnit(sb, unit, byKey, layout, showDecision: true);
        }

        if (detailed)
        {
            sb.AppendLine();
            sb.AppendLine("All placements (detailed)");
            sb.AppendLine($"  {"FormID",-9} {"FormKey",-34} {"Status",-20} {"Decision",-10} {"Unit",5}  Base / Cell / Reasons");
            foreach (var x in p)
            {
                var unit = a.Units[x.UnitIndex];
                sb.AppendLine($"  {layout.InGameFormId(x.Facts.FormKey),-9} {x.Facts.FormKey,-34} {StatusName(x.Status),-20} {DecisionName(unit.Decision),-10} {unit.Index,5}  " +
                              $"{BaseName(x.Facts)} @ {x.Facts.Cell}{ReasonSuffix(x, layout)}{ScoreSuffix(unit)}");
            }
        }
    }

    private static void RenderUnit(
        StringBuilder sb,
        SelectionUnit unit,
        IReadOnlyDictionary<Mutagen.Bethesda.Plugins.FormKey, AssessedPlacement> byKey,
        LoadOrderLayout layout,
        bool showDecision)
    {
        var decision = showDecision ? $"  {DecisionName(unit.Decision)}" : "";
        if (unit.IsGroup)
        {
            var members = unit.Members.Select(k => byKey[k].Facts).ToList();
            var first = members[0];
            var cells = members.Select(m => m.Cell.CellFormKey).Distinct().Count();
            var span = cells > 1 ? $"  spans {cells} cells (location switch, not a single encounter)" : "";
            sb.AppendLine($"  group {unit.Index} [{unit.Members.Count}] {StatusName(unit.Status)}{decision}  ({unit.Kinds}) @ {first.Cell}{span}");

            var enableParents = members.Where(m => m.EnableParent is not null).GroupBy(m => m.EnableParent!.Target);
            foreach (var g in enableParents)
                sb.AppendLine($"      enable parent  {Target(g.Key, g.First().EnableParent!.TargetDescription, layout)}  x{g.Count()}");

            var sharedTargets = members
                .SelectMany(m => m.LinkedReferenceTargets.Concat(m.ActivateParentTargets).Distinct()
                    .Select(t => (Target: t, Description: m.TargetDescriptions.GetValueOrDefault(t, "?"))))
                .GroupBy(t => t.Target)
                .Where(g => g.Count() > 1);
            foreach (var g in sharedTargets)
                sb.AppendLine($"      shared link    {Target(g.Key, g.First().Description, layout)}  x{g.Count()}");

            foreach (var x in members.Select(m => byKey[m.FormKey]))
                sb.AppendLine($"      {layout.InGameFormId(x.Facts.FormKey),-9} {x.Facts.FormKey,-34} {BaseName(x.Facts)}{(cells > 1 ? $" @ {x.Facts.Cell}" : "")}{ReasonSuffix(x, layout)}");
        }
        else
        {
            var x = byKey[unit.Members[0]];
            sb.AppendLine($"  {layout.InGameFormId(x.Facts.FormKey),-9} {x.Facts.FormKey,-34} {StatusName(unit.Status),-20}{decision} {BaseName(x.Facts)} @ {x.Facts.Cell}{ReasonSuffix(x, layout)}");
        }
    }

    private static string ReasonSuffix(AssessedPlacement x, LoadOrderLayout layout)
    {
        if (x.Reasons.Count == 0) return "";
        var parts = x.Reasons.Select(r => r switch
        {
            PlacementReason.QuestScript => $"QuestScript({string.Join(",", x.Facts.Scripts.Where(s => s.OwningQuestEditorId is not null).Select(s => $"{s.Name} -> {s.OwningQuestEditorId}"))})",
            PlacementReason.UnknownScript => $"UnknownScript({string.Join(",", x.Facts.Scripts.Where(s => s.OwningQuestEditorId is null && !PlacementRules.SafeScripts.Contains(s.Name)).Select(s => s.Name))})",
            PlacementReason.DeathCounterScript => $"DeathCounterScript({string.Join(",", x.Facts.Scripts.Where(s => PlacementRules.UnsafeScripts.Contains(s.Name)).Select(s => s.Name))})",
            PlacementReason.UnknownLocationRefType => $"UnknownLocationRefType({string.Join(",", x.Facts.LocationRefTypes.Where(t => !t.Equals(PlacementRules.BossLocationRefType, StringComparison.OrdinalIgnoreCase)))})",
            PlacementReason.InboundReference => $"InboundReference({string.Join(", ", x.Facts.InboundReferences.Select(i => $"{i.RecordType} {i.EditorId ?? i.Source.ToString()}"))})",
            PlacementReason.EnableParentUnverified when x.Facts.EnableParent is { } ep => $"EnableParentUnverified({Target(ep.Target, ep.TargetDescription, layout)})",
            _ => r.ToString(),
        });
        return "  :: " + string.Join("; ", parts);
    }

    private static string ScoreSuffix(SelectionUnit unit) => unit.Score is { } s ? $"  score=0x{s:X16}" : "";

    /// <summary>A referenced record as "FE04DB01 000B01:Plugin.esp XMarker (persistent)", ready for xEdit.</summary>
    private static string Target(Mutagen.Bethesda.Plugins.FormKey target, string description, LoadOrderLayout layout) =>
        $"{layout.InGameFormId(target)} {target} {description}";

    private static string BaseName(PlacementFacts f) => f.BaseEditorId ?? f.BaseFormKey.ToString();

    private static int Count(IReadOnlyList<AssessedPlacement> p, PlacementStatus status) => p.Count(x => x.Status == status);

    private static IEnumerable<string> KindCounts(IEnumerable<SelectionUnit> groups) =>
        new[] { GroupKinds.SharedEnableParent, GroupKinds.SharedLinkTarget, GroupKinds.ExactPosition }
            .Select(k => (k, n: groups.Count(g => g.Kinds.HasFlag(k))))
            .Where(t => t.n > 0)
            .Select(t => $"{t.k} {t.n}");

    private static string Pct(double value) => value.ToString("0.##", CultureInfo.InvariantCulture) + "%";

    public static string LayoutName(PluginLayout layout) => layout switch
    {
        PluginLayout.Simple => "Simple",
        PluginLayout.SimpleWithLinks => "Simple + links",
        PluginLayout.Grouped => "Grouped",
        PluginLayout.ProbablyNotCreatureMod => "Probably not a creature mod",
        _ => layout.ToString(),
    };

    private static string StatusName(PlacementStatus status) => status switch
    {
        PlacementStatus.Eligible => "Eligible",
        PlacementStatus.Grouped => "Grouped",
        PlacementStatus.NeedsInvestigation => "NeedsInvestigation",
        PlacementStatus.Excluded => "Excluded",
        _ => status.ToString(),
    };

    private static string DecisionName(SelectionDecision decision) => decision switch
    {
        SelectionDecision.Selected => "SELECTED",
        SelectionDecision.Rejected => "REJECTED",
        _ => "-",
    };
}
