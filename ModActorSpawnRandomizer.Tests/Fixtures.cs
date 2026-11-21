using Mutagen.Bethesda.Plugins;

namespace ModActorSpawnRandomizer.Tests;

internal static class Fixtures
{
    public static readonly ModKey Plugin = ModKey.FromFileName("Creatures.esp");
    public static readonly ModKey Skyrim = ModKey.FromFileName("Skyrim.esm");
    public static readonly FormKey TestCell = new(Skyrim, 0x0371DE);

    /// <summary>An ordinary placement: no links, scripts or flags, at a unique position.</summary>
    public static PlacementFacts Plain(uint id) => new()
    {
        FormKey = new FormKey(Plugin, id),
        BaseFormKey = new FormKey(Plugin, 0x800),
        BaseEditorId = "TestCreature",
        Cell = new CellInfo(TestCell, "TestCell", true, null, null, null),
        Position = new Position(id * 100f, 0f, 0f),
        WinningPlugin = Plugin,
    };

    public static FormKey Ref(uint id) => new(Plugin, id);
}
