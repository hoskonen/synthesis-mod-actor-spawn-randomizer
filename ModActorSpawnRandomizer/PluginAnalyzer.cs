using System.Security.Cryptography;
using System.Text;
using Mutagen.Bethesda.Plugins;

namespace ModActorSpawnRandomizer;

/// <summary>
/// Turns discovered placements into a plugin analysis. Safety eligibility is decided first and
/// never depends on the seed; the seeded selection is then simulated over eligible units only.
/// </summary>
public static class PluginAnalyzer
{
    /// <summary>Below this many eligible units the kept share varies noticeably between seeds.</summary>
    public const int SmallPluginUnitThreshold = 30;

    public static PluginAnalysis Analyze(
        ModKey plugin,
        IReadOnlyList<PlacementFacts> placements,
        int overridesOfOtherPlacements,
        double probability,
        int seed)
    {
        DeterministicSelector.ValidateProbability(probability);

        var ownReasons = placements.ToDictionary(p => p.FormKey, PlacementRules.Evaluate);
        var groups = EncounterGrouping.Build(placements);

        var units = new List<SelectionUnit>();
        var assessed = new List<AssessedPlacement>();

        foreach (var group in groups)
        {
            var index = units.Count;
            var memberStatuses = group.Members.Select(m => PlacementRules.StatusOf(ownReasons[m.FormKey])).ToList();
            var unitStatus = Worst(memberStatuses);
            var isGroup = group.Members.Count > 1;

            if (unitStatus == PlacementStatus.Eligible && isGroup) unitStatus = PlacementStatus.Grouped;

            foreach (var member in group.Members)
            {
                var reasons = ownReasons[member.FormKey].ToList();
                var own = PlacementRules.StatusOf(reasons);
                if (own == PlacementStatus.Eligible && unitStatus == PlacementStatus.Excluded)
                    reasons.Add(PlacementReason.GroupMemberExcluded);
                else if (own == PlacementStatus.Eligible && unitStatus == PlacementStatus.NeedsInvestigation)
                    reasons.Add(PlacementReason.GroupMemberNeedsInvestigation);
                else if (own == PlacementStatus.NeedsInvestigation && unitStatus == PlacementStatus.Excluded)
                    reasons.Add(PlacementReason.GroupMemberExcluded);

                assessed.Add(new AssessedPlacement(member, unitStatus, reasons, index));
            }

            var key = EncounterGrouping.UnitKey(group);
            var selectable = unitStatus is PlacementStatus.Eligible or PlacementStatus.Grouped;
            ulong? score = selectable ? DeterministicSelector.ComputeScore(key, seed) : null;
            var decision = !selectable
                ? SelectionDecision.NotApplicable
                : DeterministicSelector.IsSelected(key, seed, probability)
                    ? SelectionDecision.Selected
                    : SelectionDecision.Rejected;

            units.Add(new SelectionUnit(
                index,
                key,
                group.Members.Select(m => m.FormKey).ToList(),
                group.Kinds,
                unitStatus,
                decision,
                score));
        }

        var analysis = new PluginAnalysis
        {
            Plugin = plugin,
            Probability = probability,
            Seed = seed,
            Placements = assessed.OrderBy(a => a.Facts.FormKey.ID).ToList(),
            Units = units,
            OverridesOfOtherPlacements = overridesOfOtherPlacements,
            Fingerprint = Fingerprint(placements),
        };

        return analysis with
        {
            Layout = ClassifyLayout(analysis),
            Warnings = BuildWarnings(analysis),
            Notes = BuildNotes(analysis),
        };
    }

    public static PlacementStatus Worst(IEnumerable<PlacementStatus> statuses)
    {
        var list = statuses.ToList();
        if (list.Contains(PlacementStatus.Excluded)) return PlacementStatus.Excluded;
        if (list.Contains(PlacementStatus.NeedsInvestigation)) return PlacementStatus.NeedsInvestigation;
        return PlacementStatus.Eligible;
    }

    /// <summary>
    /// Describes the plugin's structure. This is a diagnostic only: it never makes placements
    /// eligible or ineligible; that is decided per placement.
    /// </summary>
    public static PluginLayout ClassifyLayout(PluginAnalysis a)
    {
        var count = a.Placements.Count;
        var inbound = a.Placements.Count(p => p.Facts.InboundReferences.Count > 0);
        if (count == 0 || inbound * 2 > count || a.OverridesOfOtherPlacements > count)
            return PluginLayout.ProbablyNotCreatureMod;

        if (a.Units.Any(u => u.IsGroup))
            return PluginLayout.Grouped;

        if (a.Placements.Any(p =>
                p.Facts.Scripts.Count > 0 ||
                p.Facts.LinkedReferenceTargets.Count > 0 ||
                p.Facts.ActivateParentTargets.Count > 0 ||
                p.Facts.EnableParent is not null))
            return PluginLayout.SimpleWithLinks;

        return PluginLayout.Simple;
    }

    private static IReadOnlyList<string> BuildWarnings(PluginAnalysis a)
    {
        var warnings = new List<string>();
        var count = a.Placements.Count;

        if (count == 0)
            warnings.Add("The plugin creates no placed NPCs; nothing to randomise.");

        var investigation = a.Placements.Where(p => p.Status == PlacementStatus.NeedsInvestigation).ToList();
        if (investigation.Count > 0)
            warnings.Add($"{investigation.Count} placements need investigation and are left untouched ({TopReasons(investigation, ReasonSeverity.Investigation)}).");

        var safetyExcluded = a.Placements
            .Where(p => p.Status == PlacementStatus.Excluded &&
                        p.Reasons.Any(r => PlacementRules.SeverityOf(r) == ReasonSeverity.Exclusion && !PlacementRules.IsInactiveReason(r)))
            .ToList();
        if (safetyExcluded.Count > 0)
            warnings.Add($"{safetyExcluded.Count} placements are excluded by safety rules ({TopReasons(safetyExcluded, ReasonSeverity.Exclusion)}).");

        var investigatedGroups = a.Units.Count(u => u.IsGroup && u.Status != PlacementStatus.Grouped);
        if (investigatedGroups > 0)
            warnings.Add($"{investigatedGroups} encounter groups are not selectable and stay unchanged as whole groups.");

        if (a.OverridesOfOtherPlacements > 0)
            warnings.Add($"The plugin also overrides {a.OverridesOfOtherPlacements} placed NPCs from other plugins; these are never touched.");

        var inboundSources = a.Placements
            .SelectMany(p => p.Facts.InboundReferences)
            .Select(r => r.Source.ModKey)
            .Distinct()
            .ToList();
        if (inboundSources.Count > 0)
            warnings.Add($"Other records point at this plugin's placements (from: {string.Join(", ", inboundSources)}).");

        if (count > 0 && (investigation.Count + safetyExcluded.Count) * 4 > count)
            warnings.Add("More than 25% of placements are not eligible; review whether this is an ordinary creature mod.");

        return warnings;
    }

    private static IReadOnlyList<string> BuildNotes(PluginAnalysis a)
    {
        var notes = new List<string>();
        var eligibleUnits = a.Units.Count(u => u.Decision != SelectionDecision.NotApplicable);
        if (eligibleUnits is > 0 and < SmallPluginUnitThreshold)
            notes.Add($"Only {eligibleUnits} selectable units: the kept share will vary noticeably between seeds.");

        var overriddenBy = a.Placements
            .Where(p => p.Facts.WinningPlugin != a.Plugin && !p.Facts.WinningPlugin.IsNull)
            .GroupBy(p => p.Facts.WinningPlugin)
            .Select(g => $"{g.Key} ({g.Count()})")
            .ToList();
        if (overriddenBy.Count > 0)
            notes.Add($"Placements overridden by later plugins (classified from the winning record): {string.Join(", ", overriddenBy)}.");

        var inactive = a.Placements.Count(p => p.Reasons.Any(PlacementRules.IsInactiveReason));
        if (inactive > 0)
            notes.Add($"{inactive} placements are already disabled or deleted in the winning record.");

        return notes;
    }

    private static string TopReasons(IEnumerable<AssessedPlacement> placements, ReasonSeverity severity) =>
        string.Join(", ", placements
            .SelectMany(p => p.Reasons.Where(r => PlacementRules.SeverityOf(r) == severity))
            .GroupBy(r => r)
            .OrderByDescending(g => g.Count())
            .Select(g => $"{g.Key} {g.Count()}"));

    /// <summary>Short hash of the discovered placement set; changes when the plugin's placements change.</summary>
    public static string Fingerprint(IEnumerable<PlacementFacts> placements)
    {
        var text = string.Join("\n", placements
            .OrderBy(p => p.FormKey.ID)
            .Select(p => $"{p.FormKey.ID:X6}|{p.BaseFormKey}"));
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(text));
        return Convert.ToHexString(hash, 0, 4);
    }
}
