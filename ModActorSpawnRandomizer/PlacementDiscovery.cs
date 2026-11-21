using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Cache;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;

namespace ModActorSpawnRandomizer;

/// <summary>
/// Read-only discovery of the placed NPCs a plugin creates. Identity comes from the source plugin
/// (FormKey.ModKey == plugin); every other fact comes from the winning load-order record.
/// </summary>
public static class PlacementDiscovery
{
    private const uint PersistentFlag = 0x400;
    private const uint InitiallyDisabledFlag = 0x800;

    public sealed record Result(IReadOnlyList<PlacementFacts> Placements, int OverridesOfOtherPlacements);

    public static Result Discover(
        ISkyrimModGetter source,
        IReadOnlyList<ISkyrimModGetter> loadOrder,
        ILinkCache<ISkyrimMod, ISkyrimModGetter> linkCache)
    {
        var plugin = source.ModKey;
        var sourceRecords = source.EnumerateMajorRecords<IPlacedNpcGetter>().ToList();
        var own = sourceRecords
            .Where(r => r.FormKey.ModKey == plugin)
            .Select(r => r.FormKey)
            .Distinct()
            .ToList();
        var overrides = sourceRecords.Count(r => r.FormKey.ModKey != plugin);

        var inbound = FindInboundReferences(plugin, own.ToHashSet(), loadOrder);

        var placements = own
            .Select(formKey => Describe(formKey, source, linkCache, inbound))
            .OrderBy(p => p.FormKey.ID)
            .ToList();

        return new Result(placements, overrides);
    }

    private static PlacementFacts Describe(
        FormKey formKey,
        ISkyrimModGetter source,
        ILinkCache<ISkyrimMod, ISkyrimModGetter> linkCache,
        IReadOnlyDictionary<FormKey, List<InboundReference>> inbound)
    {
        if (!linkCache.TryResolveContext<IPlacedNpc, IPlacedNpcGetter>(formKey, out var context))
        {
            // Should not happen for a record the source plugin defines; fall back to the source copy.
            context = source.EnumerateMajorRecordContexts<IPlacedNpc, IPlacedNpcGetter>(linkCache)
                .First(c => c.Record.FormKey == formKey);
        }

        var record = context.Record;
        var flags = (uint)record.MajorRecordFlagsRaw;

        INpcGetter? npc = null;
        var baseResolved = !record.Base.IsNull && linkCache.TryResolve<INpcGetter>(record.Base.FormKey, out npc);

        var linkedTargets = record.LinkedReferences
            .Select(l => l.Reference.FormKey)
            .Where(fk => !fk.IsNull)
            .ToList();
        var activateParentTargets = record.ActivateParents?.Parents
            .Select(p => p.Reference.FormKey)
            .Where(fk => !fk.IsNull)
            .ToList() ?? [];
        var targetDescriptions = linkedTargets
            .Concat(activateParentTargets)
            .Distinct()
            .ToDictionary(fk => fk, fk => DescribeReference(fk, linkCache));

        return new PlacementFacts
        {
            FormKey = formKey,
            BaseFormKey = record.Base.FormKey,
            BaseEditorId = npc?.EditorID,
            BaseResolved = baseResolved,
            BaseUnique = npc?.Configuration.Flags.HasFlag(NpcConfiguration.Flag.Unique) ?? false,
            BaseEssential = npc?.Configuration.Flags.HasFlag(NpcConfiguration.Flag.Essential) ?? false,
            Cell = DescribeCell(context, linkCache),
            Position = record.Placement is { } placement
                ? new Position(placement.Position.X, placement.Position.Y, placement.Position.Z)
                : null,
            WinningPlugin = context.ModKey,
            Deleted = record.IsDeleted,
            Persistent = (flags & PersistentFlag) != 0,
            InitiallyDisabled = (flags & InitiallyDisabledFlag) != 0,
            Scripts = DescribeScripts(record, linkCache),
            LinkedReferenceTargets = linkedTargets,
            ActivateParentTargets = activateParentTargets,
            TargetDescriptions = targetDescriptions,
            EnableParent = record.EnableParent is { } ep && !ep.Reference.IsNull
                ? new EnableParentFacts(
                    ep.Reference.FormKey,
                    ep.Flags.HasFlag(EnableParent.Flag.SetEnableStateToOppositeOfParent),
                    DescribeReference(ep.Reference.FormKey, linkCache))
                : null,
            LocationRefTypes = record.LocationRefTypes?
                .Select(l => linkCache.TryResolve<ILocationReferenceTypeGetter>(l.FormKey, out var t) && t.EditorID is { } id
                    ? id
                    : l.FormKey.ToString())
                .ToList() ?? [],
            HasPersistentLocation = !record.PersistentLocation.IsNull,
            HasMerchantContainer = !record.MerchantContainer.IsNull,
            InboundReferences = inbound.TryGetValue(formKey, out var list) ? list : [],
        };
    }

    private static IReadOnlyList<ScriptFacts> DescribeScripts(IPlacedNpcGetter record, ILinkCache linkCache)
    {
        if (record.VirtualMachineAdapter is not { } vmad) return [];
        return vmad.Scripts
            .Select(script =>
            {
                string? quest = null;
                foreach (var property in script.Properties.OfType<IScriptObjectPropertyGetter>())
                {
                    if (linkCache.TryResolve<IQuestGetter>(property.Object.FormKey, out var q))
                    {
                        quest = q.EditorID ?? q.FormKey.ToString();
                        break;
                    }
                }

                return new ScriptFacts(script.Name, quest);
            })
            .ToList();
    }

    private static CellInfo DescribeCell(
        IModContext<ISkyrimMod, ISkyrimModGetter, IPlacedNpc, IPlacedNpcGetter> context,
        ILinkCache linkCache)
    {
        var node = context.Parent;
        while (node is not null && node.Record is not ICellGetter) node = node.Parent;
        if (node?.Record is not ICellGetter cell)
            return new CellInfo(FormKey.Null, null, false, null, null, null);

        var editorId = linkCache.TryResolve<ICellGetter>(cell.FormKey, out var winningCell)
            ? winningCell.EditorID
            : cell.EditorID;
        var interior = cell.Flags.HasFlag(Cell.Flag.IsInteriorCell);

        string? worldspace = null;
        if (!interior)
        {
            var w = node.Parent;
            while (w is not null && w.Record is not IWorldspaceGetter) w = w.Parent;
            if (w?.Record is IWorldspaceGetter ws)
            {
                worldspace = linkCache.TryResolve<IWorldspaceGetter>(ws.FormKey, out var winningWs)
                    ? winningWs.EditorID
                    : ws.EditorID;
            }
        }

        return new CellInfo(
            cell.FormKey,
            editorId,
            interior,
            worldspace,
            interior ? null : cell.Grid?.Point.X,
            interior ? null : cell.Grid?.Point.Y);
    }

    private static string DescribeReference(FormKey formKey, ILinkCache linkCache)
    {
        if (!linkCache.TryResolve<IPlacedGetter>(formKey, out var placed))
            return $"unresolved {formKey}";

        var flags = (uint)placed.MajorRecordFlagsRaw;
        var baseName = placed switch
        {
            IPlacedObjectGetter o => EditorIdOf(o.Base.FormKey, linkCache),
            IPlacedNpcGetter n => EditorIdOf(n.Base.FormKey, linkCache),
            _ => placed.GetType().Name,
        };
        var state = string.Join(", ", new[]
        {
            (flags & PersistentFlag) != 0 ? "persistent" : null,
            (flags & InitiallyDisabledFlag) != 0 ? "initially disabled" : null,
        }.OfType<string>());
        return $"{baseName}{(state.Length > 0 ? $" ({state})" : "")}";
    }

    private static string EditorIdOf(FormKey formKey, ILinkCache linkCache) =>
        linkCache.TryResolve<IMajorRecordGetter>(formKey, out var r) && r.EditorID is { } id ? id : formKey.ToString();

    /// <summary>
    /// Finds records that point at the given placements. Only the plugin itself and plugins that list it
    /// as a master can reference its FormIDs, so the rest of the load order is not scanned.
    /// Cells, worldspaces and dialog topics are skipped as sources because their form links include those
    /// of the records nested inside them; the nested records are scanned individually instead.
    /// </summary>
    public static IReadOnlyDictionary<FormKey, List<InboundReference>> FindInboundReferences(
        ModKey plugin,
        IReadOnlySet<FormKey> targets,
        IReadOnlyList<ISkyrimModGetter> loadOrder)
    {
        var result = new Dictionary<FormKey, List<InboundReference>>();
        if (targets.Count == 0) return result;

        var candidates = loadOrder.Where(m =>
            m.ModKey == plugin || m.ModHeader.MasterReferences.Any(r => r.Master == plugin));

        foreach (var mod in candidates)
        {
            foreach (var record in mod.EnumerateMajorRecords())
            {
                if (record is ICellGetter or IWorldspaceGetter or IDialogTopicGetter) continue;

                foreach (var link in record.EnumerateFormLinks())
                {
                    if (link.FormKey == record.FormKey || !targets.Contains(link.FormKey)) continue;
                    if (!result.TryGetValue(link.FormKey, out var list)) result[link.FormKey] = list = new();
                    if (list.Any(r => r.Source == record.FormKey)) continue;
                    list.Add(new InboundReference(record.FormKey, RecordTypeName(record), record.EditorID));
                }
            }
        }

        return result;
    }

    private static string RecordTypeName(IMajorRecordGetter record)
    {
        var name = record.GetType().Name;
        foreach (var suffix in new[] { "BinaryOverlay", "Getter" })
            if (name.EndsWith(suffix, StringComparison.Ordinal)) name = name[..^suffix.Length];
        return name;
    }
}
