using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;

namespace ModActorSpawnRandomizer;

/// <summary>
/// Maps plugins to their runtime slot so the report can print the FormIDs shown in xEdit and the console
/// (e.g. FE04DAFB for a light plugin, 1A000AFB for a full one).
/// </summary>
public sealed class LoadOrderLayout
{
    private readonly Dictionary<ModKey, (bool Light, int Index)> _slots = new();

    public LoadOrderLayout(IEnumerable<(ModKey Plugin, bool IsLight)> activePluginsInOrder)
    {
        int full = 0, light = 0;
        foreach (var (plugin, isLight) in activePluginsInOrder)
        {
            if (_slots.ContainsKey(plugin)) continue;
            _slots[plugin] = isLight ? (true, light++) : (false, full++);
        }
    }

    public static LoadOrderLayout FromMods(IEnumerable<ISkyrimModGetter> activeModsInOrder) =>
        new(activeModsInOrder.Select(m => (m.ModKey, IsLight(m))));

    public static bool IsLight(ISkyrimModGetter mod) =>
        mod.ModKey.Type == ModType.Light || mod.ModHeader.Flags.HasFlag(SkyrimModHeader.HeaderFlag.Small);

    public string Slot(ModKey plugin) =>
        _slots.TryGetValue(plugin, out var s)
            ? s.Light ? $"FE:{s.Index:X3}" : $"{s.Index:X2}"
            : "not loaded";

    /// <summary>Runtime FormID, or "??" + local ID when the plugin's slot is unknown.</summary>
    public string InGameFormId(FormKey formKey)
    {
        if (!_slots.TryGetValue(formKey.ModKey, out var s))
            return $"??{formKey.ID:X6}";
        return s.Light
            ? $"FE{s.Index:X3}{formKey.ID & 0xFFF:X3}"
            : $"{s.Index:X2}{formKey.ID & 0xFFFFFF:X6}";
    }
}
