using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Synthesis.Settings;

namespace ModActorSpawnRandomizer;

public sealed class Settings
{
    [SynthesisOrder]
    [SynthesisTooltip("Global seed. The same seed, settings and plugins always produce the same selection.")]
    public int Seed = 38174;

    [SynthesisOrder]
    [SynthesisTooltip("List every analysed placement in the report, not only summaries and non-eligible placements.")]
    public bool DetailedReport = false;

    [SynthesisOrder]
    [SynthesisTooltip("Plugins to analyse. Only placed NPCs created by these plugins are considered.")]
    public List<PluginSelection> Plugins = new();
}

public sealed class PluginSelection
{
    [SynthesisOrder]
    [SynthesisTooltip("Plugin whose own placed NPCs (ACHR) are analysed.")]
    public ModKey Plugin = ModKey.Null;

    [SynthesisOrder]
    [SynthesisTooltip("Percentage (0-100) of eligible placements to keep active.")]
    public double Probability = 50.0;
}

public static class SettingsValidator
{
    public static IReadOnlyList<string> Validate(Settings settings)
    {
        var errors = new List<string>();
        if (settings.Plugins.Count == 0)
        {
            errors.Add("No plugins selected. Add at least one plugin to the Plugins list.");
        }

        for (var i = 0; i < settings.Plugins.Count; i++)
        {
            var entry = settings.Plugins[i];
            if (entry.Plugin.IsNull)
            {
                errors.Add($"Plugins[{i}] has no plugin selected.");
            }

            if (!double.IsFinite(entry.Probability) || entry.Probability < 0.0 || entry.Probability > 100.0)
            {
                errors.Add($"Plugins[{i}] ({entry.Plugin}) probability {entry.Probability} must be from 0 through 100.");
            }
        }

        foreach (var duplicate in settings.Plugins
                     .Where(p => !p.Plugin.IsNull)
                     .GroupBy(p => p.Plugin)
                     .Where(g => g.Count() > 1))
        {
            errors.Add($"{duplicate.Key} is selected {duplicate.Count()} times.");
        }

        return errors;
    }
}
