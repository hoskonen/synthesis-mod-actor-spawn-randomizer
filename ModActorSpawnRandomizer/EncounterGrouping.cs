using Mutagen.Bethesda.Plugins;

namespace ModActorSpawnRandomizer;

/// <summary>
/// Builds encounter groups from data alone: placements that share an outgoing link target
/// (linked reference or activate parent), share an enable parent, or stand at exactly the
/// same position in the same cell are one group. Groups are connected components.
/// </summary>
public static class EncounterGrouping
{
    public sealed record Group(IReadOnlyList<PlacementFacts> Members, GroupKinds Kinds);

    public static IReadOnlyList<Group> Build(IReadOnlyList<PlacementFacts> placements)
    {
        var parent = Enumerable.Range(0, placements.Count).ToArray();
        int Find(int i) => parent[i] == i ? i : parent[i] = Find(parent[i]);

        var kinds = new GroupKinds[placements.Count];
        var firstByKey = new Dictionary<(GroupKinds Kind, object Key), int>();

        void Join(int index, GroupKinds kind, object key)
        {
            if (firstByKey.TryGetValue((kind, key), out var other))
            {
                kinds[index] |= kind;
                kinds[other] |= kind;
                var a = Find(index);
                var b = Find(other);
                if (a != b) parent[Math.Max(a, b)] = Math.Min(a, b);
            }
            else
            {
                firstByKey[(kind, key)] = index;
            }
        }

        for (var i = 0; i < placements.Count; i++)
        {
            var p = placements[i];
            foreach (var target in p.LinkedReferenceTargets.Concat(p.ActivateParentTargets).Distinct())
                Join(i, GroupKinds.SharedLinkTarget, target);
            if (p.EnableParent is { } ep)
                Join(i, GroupKinds.SharedEnableParent, ep.Target);
            if (p.Position is { } pos)
                Join(i, GroupKinds.ExactPosition, (p.Cell.CellFormKey, pos.ExactKey));
        }

        return Enumerable.Range(0, placements.Count)
            .GroupBy(Find)
            .Select(g =>
            {
                var members = g.Select(i => placements[i]).OrderBy(p => p.FormKey.ID).ToList();
                var groupKinds = members.Count > 1
                    ? g.Aggregate(GroupKinds.None, (acc, i) => acc | kinds[i])
                    : GroupKinds.None;
                return new Group(members, groupKinds);
            })
            .OrderBy(g => g.Members[0].FormKey.ID)
            .ToList();
    }

    /// <summary>Stable selection key of a unit: its lowest member FormKey.</summary>
    public static FormKey UnitKey(Group group) => group.Members[0].FormKey;
}
