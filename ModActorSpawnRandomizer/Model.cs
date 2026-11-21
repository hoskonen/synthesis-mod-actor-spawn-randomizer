using Mutagen.Bethesda.Plugins;

namespace ModActorSpawnRandomizer;

/// <summary>Read-only snapshot of one placed NPC, taken from its winning record.</summary>
public sealed record PlacementFacts
{
    public required FormKey FormKey { get; init; }
    public required FormKey BaseFormKey { get; init; }
    public string? BaseEditorId { get; init; }
    public bool BaseResolved { get; init; } = true;
    public bool BaseUnique { get; init; }
    public bool BaseEssential { get; init; }
    public required CellInfo Cell { get; init; }
    public Position? Position { get; init; }
    public ModKey WinningPlugin { get; init; }
    public bool Deleted { get; init; }
    public bool Persistent { get; init; }
    public bool InitiallyDisabled { get; init; }
    public IReadOnlyList<ScriptFacts> Scripts { get; init; } = [];
    public IReadOnlyList<FormKey> LinkedReferenceTargets { get; init; } = [];
    public IReadOnlyList<FormKey> ActivateParentTargets { get; init; } = [];
    public EnableParentFacts? EnableParent { get; init; }
    public IReadOnlyList<string> LocationRefTypes { get; init; } = [];
    public bool HasPersistentLocation { get; init; }
    public bool HasMerchantContainer { get; init; }
    public IReadOnlyList<InboundReference> InboundReferences { get; init; } = [];

    /// <summary>Short descriptions ("XMarker (persistent)") of referenced link targets, for reporting only.</summary>
    public IReadOnlyDictionary<FormKey, string> TargetDescriptions { get; init; } = new Dictionary<FormKey, string>();
}

public readonly record struct Position(float X, float Y, float Z)
{
    /// <summary>Exact identity of the position (float bit patterns), as used by the Specters pairing.</summary>
    public (int, int, int) ExactKey =>
        (BitConverter.SingleToInt32Bits(X), BitConverter.SingleToInt32Bits(Y), BitConverter.SingleToInt32Bits(Z));
}

public sealed record CellInfo(FormKey CellFormKey, string? EditorId, bool Interior, string? Worldspace, int? GridX, int? GridY)
{
    public override string ToString()
    {
        var name = EditorId ?? CellFormKey.ToString();
        if (Interior) return $"INT {name}";
        var grid = GridX is { } x && GridY is { } y ? $" ({x},{y})" : "";
        return $"EXT {Worldspace ?? "?"}{grid} {name}";
    }
}

public sealed record ScriptFacts(string Name, string? OwningQuestEditorId);

public sealed record EnableParentFacts(FormKey Target, bool OppositeOfParent, string TargetDescription);

public sealed record InboundReference(FormKey Source, string RecordType, string? EditorId);

public enum PlacementStatus
{
    /// <summary>Selectable on its own.</summary>
    Eligible,

    /// <summary>Selectable, but only together with the other members of its encounter group.</summary>
    Grouped,

    /// <summary>Structure not validated yet; left untouched until it is.</summary>
    NeedsInvestigation,

    /// <summary>A safety rule (or inactive state) keeps it untouched.</summary>
    Excluded,
}

public enum ReasonSeverity
{
    Investigation,
    Exclusion,
}

public enum PlacementReason
{
    // Exclusions
    Deleted,
    AlreadyDisabled,
    UniqueOrEssentialBase,
    InboundReference,
    QuestScript,
    DeathCounterScript,
    BossLocationRefType,
    OppositeEnableParent,
    GroupMemberExcluded,

    // Needs investigation
    EnableParentUnverified,
    UnknownScript,
    UnknownLocationRefType,
    PersistentReference,
    PersistentLocation,
    MerchantContainer,
    UnresolvedBase,
    MissingPosition,
    GroupMemberNeedsInvestigation,
}

[Flags]
public enum GroupKinds
{
    None = 0,
    SharedLinkTarget = 1,
    SharedEnableParent = 2,
    ExactPosition = 4,
}

public enum SelectionDecision
{
    /// <summary>Kept active by the seeded selection.</summary>
    Selected,

    /// <summary>Would be disabled by the future apply phase.</summary>
    Rejected,

    /// <summary>Not selectable (excluded or needs investigation); would stay unchanged.</summary>
    NotApplicable,
}

public enum PluginLayout
{
    Simple,
    SimpleWithLinks,
    Grouped,
    ProbablyNotCreatureMod,
}

public sealed record AssessedPlacement(
    PlacementFacts Facts,
    PlacementStatus Status,
    IReadOnlyList<PlacementReason> Reasons,
    int UnitIndex);

public sealed record SelectionUnit(
    int Index,
    FormKey Key,
    IReadOnlyList<FormKey> Members,
    GroupKinds Kinds,
    PlacementStatus Status,
    SelectionDecision Decision,
    ulong? Score)
{
    public bool IsGroup => Members.Count > 1;
}

public sealed record PluginAnalysis
{
    public required ModKey Plugin { get; init; }
    public required double Probability { get; init; }
    public required int Seed { get; init; }
    public bool Found { get; init; } = true;
    public IReadOnlyList<AssessedPlacement> Placements { get; init; } = [];
    public IReadOnlyList<SelectionUnit> Units { get; init; } = [];
    public int OverridesOfOtherPlacements { get; init; }
    public PluginLayout Layout { get; init; }
    public IReadOnlyList<string> Warnings { get; init; } = [];
    public IReadOnlyList<string> Notes { get; init; } = [];
    public string Fingerprint { get; init; } = "";

    public bool HasWarnings => Warnings.Count > 0;
}
