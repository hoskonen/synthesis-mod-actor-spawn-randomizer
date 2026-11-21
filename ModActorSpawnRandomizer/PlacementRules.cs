namespace ModActorSpawnRandomizer;

/// <summary>
/// Generic per-placement safety rules (docs/01-feasibility.md, section 5.3).
/// Anything not positively recognised as safe yields a reason; unknown structures are never eligible.
/// </summary>
public static class PlacementRules
{
    /// <summary>Reference scripts observed on ordinary creature placements in the investigated mods.</summary>
    public static readonly IReadOnlySet<string> SafeScripts = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "masterambushscript",
        "DLC1SkeletonAmbushScript",
        "defaultGhostScript",
        "AutomaticLightSwitchScript",
    };

    /// <summary>Scripts that make other content depend on the actor (e.g. a counter waiting for its death).</summary>
    public static readonly IReadOnlySet<string> UnsafeScripts = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "defaultCounterIncrementOnDeath",
    };

    public const string BossLocationRefType = "Boss";

    public static IReadOnlyList<PlacementReason> Evaluate(PlacementFacts facts)
    {
        var reasons = new List<PlacementReason>();

        if (facts.Deleted) reasons.Add(PlacementReason.Deleted);
        if (facts.InitiallyDisabled) reasons.Add(PlacementReason.AlreadyDisabled);
        if (!facts.BaseResolved) reasons.Add(PlacementReason.UnresolvedBase);
        if (facts.BaseUnique || facts.BaseEssential) reasons.Add(PlacementReason.UniqueOrEssentialBase);
        if (facts.InboundReferences.Count > 0) reasons.Add(PlacementReason.InboundReference);
        if (facts.Position is null) reasons.Add(PlacementReason.MissingPosition);
        if (facts.Persistent) reasons.Add(PlacementReason.PersistentReference);
        if (facts.HasPersistentLocation) reasons.Add(PlacementReason.PersistentLocation);
        if (facts.HasMerchantContainer) reasons.Add(PlacementReason.MerchantContainer);

        foreach (var script in facts.Scripts)
        {
            if (UnsafeScripts.Contains(script.Name)) reasons.Add(PlacementReason.DeathCounterScript);
            else if (script.OwningQuestEditorId is not null) reasons.Add(PlacementReason.QuestScript);
            else if (!SafeScripts.Contains(script.Name)) reasons.Add(PlacementReason.UnknownScript);
        }

        foreach (var locRefType in facts.LocationRefTypes)
        {
            reasons.Add(string.Equals(locRefType, BossLocationRefType, StringComparison.OrdinalIgnoreCase)
                ? PlacementReason.BossLocationRefType
                : PlacementReason.UnknownLocationRefType);
        }

        if (facts.EnableParent is { } enableParent)
        {
            if (enableParent.OppositeOfParent) reasons.Add(PlacementReason.OppositeEnableParent);
            // Whether InitiallyDisabled is honoured on a child with an enable parent is unverified in game.
            reasons.Add(PlacementReason.EnableParentUnverified);
        }

        return reasons.Distinct().ToList();
    }

    public static ReasonSeverity SeverityOf(PlacementReason reason) => reason switch
    {
        PlacementReason.Deleted or
        PlacementReason.AlreadyDisabled or
        PlacementReason.UniqueOrEssentialBase or
        PlacementReason.InboundReference or
        PlacementReason.QuestScript or
        PlacementReason.DeathCounterScript or
        PlacementReason.BossLocationRefType or
        PlacementReason.OppositeEnableParent or
        PlacementReason.GroupMemberExcluded => ReasonSeverity.Exclusion,
        _ => ReasonSeverity.Investigation,
    };

    /// <summary>Status from the placement's own reasons (before encounter grouping).</summary>
    public static PlacementStatus StatusOf(IReadOnlyCollection<PlacementReason> reasons)
    {
        if (reasons.Any(r => SeverityOf(r) == ReasonSeverity.Exclusion)) return PlacementStatus.Excluded;
        if (reasons.Count > 0) return PlacementStatus.NeedsInvestigation;
        return PlacementStatus.Eligible;
    }

    /// <summary>Inactive records are excluded but are not a safety concern worth a warning.</summary>
    public static bool IsInactiveReason(PlacementReason reason) =>
        reason is PlacementReason.Deleted or PlacementReason.AlreadyDisabled;
}
