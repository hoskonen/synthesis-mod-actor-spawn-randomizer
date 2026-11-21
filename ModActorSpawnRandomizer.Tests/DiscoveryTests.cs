using Microsoft.VisualStudio.TestTools.UnitTesting;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using Noggog;

namespace ModActorSpawnRandomizer.Tests;

/// <summary>Discovery against small in-memory plugins built with Mutagen.</summary>
[TestClass]
public sealed class DiscoveryTests
{
    private static readonly ModKey MasterKey = ModKey.FromFileName("Master.esm");
    private static readonly ModKey CreatureKey = ModKey.FromFileName("Creatures.esp");
    private static readonly ModKey PatchKey = ModKey.FromFileName("Patch.esp");

    private sealed class World
    {
        public SkyrimMod Master { get; } = new(MasterKey, SkyrimRelease.SkyrimSE);
        public SkyrimMod Creatures { get; } = new(CreatureKey, SkyrimRelease.SkyrimSE);
        public SkyrimMod Patch { get; } = new(PatchKey, SkyrimRelease.SkyrimSE);
        public Cell MasterCell { get; }
        public Cell CreatureCellOverride { get; }
        public Npc Creature { get; }
        public LocationReferenceType Boss { get; }

        public World()
        {
            MasterCell = new Cell(Master) { EditorID = "TestDungeon01", Flags = Cell.Flag.IsInteriorCell };
            Master.Cells.Records.Add(Block(MasterCell));
            Boss = new LocationReferenceType(Master) { EditorID = "Boss" };
            Master.LocationReferenceTypes.Add(Boss);

            Creature = new Npc(Creatures) { EditorID = "TestCreature" };
            Creatures.Npcs.Add(Creature);
            CreatureCellOverride = new Cell(MasterCell.FormKey, SkyrimRelease.SkyrimSE)
            {
                EditorID = "TestDungeon01",
                Flags = Cell.Flag.IsInteriorCell,
            };
            Creatures.Cells.Records.Add(Block(CreatureCellOverride));
            Creatures.ModHeader.MasterReferences.Add(new MasterReference { Master = MasterKey });
            Patch.ModHeader.MasterReferences.Add(new MasterReference { Master = MasterKey });
            Patch.ModHeader.MasterReferences.Add(new MasterReference { Master = CreatureKey });
        }

        public PlacedNpc Place(float x, Action<PlacedNpc>? configure = null)
        {
            var placed = new PlacedNpc(Creatures)
            {
                Base = Creature.ToNullableLink(),
                Placement = new Placement { Position = new P3Float(x, 0, 0) },
            };
            configure?.Invoke(placed);
            CreatureCellOverride.Temporary.Add(placed);
            return placed;
        }

        public PlacementDiscovery.Result Discover()
        {
            var loadOrder = new ISkyrimModGetter[] { Master, Creatures, Patch };
            return PlacementDiscovery.Discover(Creatures, loadOrder, loadOrder.ToImmutableLinkCache());
        }

        private static CellBlock Block(Cell cell) => new()
        {
            BlockNumber = 0,
            GroupType = GroupTypeEnum.InteriorCellBlock,
            SubBlocks =
            {
                new CellSubBlock
                {
                    BlockNumber = 0,
                    GroupType = GroupTypeEnum.InteriorCellSubBlock,
                    Cells = { cell },
                },
            },
        };
    }

    [TestMethod]
    public void DiscoversOnlyPlacementsCreatedByThePlugin()
    {
        var world = new World();
        var own = world.Place(1);
        var vanillaNpc = new PlacedNpc(world.Master) { Base = world.Creature.ToNullableLink() };
        world.MasterCell.Temporary.Add(vanillaNpc);
        // The creature plugin overrides a master placement (e.g. HPE making vanilla actors persistent).
        world.CreatureCellOverride.Temporary.Add(new PlacedNpc(vanillaNpc.FormKey, SkyrimRelease.SkyrimSE)
        {
            Base = world.Creature.ToNullableLink(),
            MajorRecordFlagsRaw = 0x400,
        });

        var result = world.Discover();

        Assert.AreEqual(1, result.Placements.Count);
        Assert.AreEqual(own.FormKey, result.Placements[0].FormKey);
        Assert.AreEqual(1, result.OverridesOfOtherPlacements);
        Assert.AreEqual("TestCreature", result.Placements[0].BaseEditorId);
        Assert.IsTrue(result.Placements[0].Cell.Interior);
        Assert.AreEqual("TestDungeon01", result.Placements[0].Cell.EditorId);
    }

    [TestMethod]
    public void ReadsFactsFromWinningRecord()
    {
        var world = new World();
        var placed = world.Place(1);
        var patchCell = new Cell(world.MasterCell.FormKey, SkyrimRelease.SkyrimSE) { Flags = Cell.Flag.IsInteriorCell };
        patchCell.Temporary.Add(new PlacedNpc(placed.FormKey, SkyrimRelease.SkyrimSE)
        {
            Base = world.Creature.ToNullableLink(),
            Placement = new Placement { Position = new P3Float(1, 0, 0) },
            MajorRecordFlagsRaw = 0x800,
        });
        world.Patch.Cells.Records.Add(new CellBlock
        {
            BlockNumber = 0,
            GroupType = GroupTypeEnum.InteriorCellBlock,
            SubBlocks = { new CellSubBlock { BlockNumber = 0, GroupType = GroupTypeEnum.InteriorCellSubBlock, Cells = { patchCell } } },
        });

        var facts = world.Discover().Placements.Single();

        Assert.AreEqual(PatchKey, facts.WinningPlugin);
        Assert.IsTrue(facts.InitiallyDisabled);
    }

    [TestMethod]
    public void ExtractsLinksEnableParentScriptsAndLocationRefTypes()
    {
        var world = new World();
        var marker = new PlacedObject(world.Creatures) { MajorRecordFlagsRaw = 0xC00 };
        world.CreatureCellOverride.Persistent.Add(marker);
        var quest = new Quest(world.Master) { EditorID = "C05" };
        world.Master.Quests.Add(quest);

        var placed = world.Place(1, p =>
        {
            p.LinkedReferences.Add(new LinkedReferences { Reference = marker.ToLink<IPlacedGetter>() });
            p.EnableParent = new EnableParent
            {
                Reference = marker.ToLink<IPlacedGetter>(),
                Flags = EnableParent.Flag.SetEnableStateToOppositeOfParent,
            };
            p.LocationRefTypes = new ExtendedList<IFormLinkGetter<ILocationReferenceTypeGetter>> { world.Boss.ToLink() };
            p.VirtualMachineAdapter = new VirtualMachineAdapter
            {
                Scripts =
                {
                    new ScriptEntry
                    {
                        Name = "C05SilverHandScript",
                        Properties = { new ScriptObjectProperty { Name = "OwningQuest", Object = quest.ToLink<ISkyrimMajorRecordGetter>() } },
                    },
                    new ScriptEntry { Name = "masterambushscript" },
                },
            };
        });

        var facts = world.Discover().Placements.Single(p => p.FormKey == placed.FormKey);

        CollectionAssert.AreEqual(new[] { marker.FormKey }, facts.LinkedReferenceTargets.ToArray());
        Assert.IsNotNull(facts.EnableParent);
        Assert.AreEqual(marker.FormKey, facts.EnableParent.Target);
        Assert.IsTrue(facts.EnableParent.OppositeOfParent);
        StringAssert.Contains(facts.EnableParent.TargetDescription, "initially disabled");
        CollectionAssert.AreEqual(new[] { "Boss" }, facts.LocationRefTypes.ToArray());
        Assert.AreEqual("C05", facts.Scripts.Single(s => s.Name == "C05SilverHandScript").OwningQuestEditorId);
        Assert.IsNull(facts.Scripts.Single(s => s.Name == "masterambushscript").OwningQuestEditorId);
    }

    [TestMethod]
    public void FindsInboundReferencesFromPluginAndDependents()
    {
        var world = new World();
        var questTarget = world.Place(1);
        var enableParentTarget = world.Place(2);
        var untouched = world.Place(3);

        // A dependent patch points a quest alias at one placement.
        var quest = new Quest(world.Patch) { EditorID = "PatchQuest" };
        quest.Aliases.Add(new QuestAlias { Name = "Target", ForcedReference = questTarget.ToNullableLink<IPlacedGetter>() });
        world.Patch.Quests.Add(quest);

        // The plugin itself uses one placement as another placement's enable parent.
        world.Place(4, p => p.EnableParent = new EnableParent { Reference = enableParentTarget.ToLink<IPlacedGetter>() });

        var placements = world.Discover().Placements.ToDictionary(p => p.FormKey);

        Assert.AreEqual("PatchQuest", placements[questTarget.FormKey].InboundReferences.Single().EditorId);
        Assert.AreEqual(1, placements[enableParentTarget.FormKey].InboundReferences.Count);
        Assert.AreEqual(0, placements[untouched.FormKey].InboundReferences.Count);
    }
}
