using Mutagen.Bethesda.Plugins.Cache;
using Mutagen.Bethesda.Skyrim;

namespace ModActorSpawnRandomizer;

/// <summary>Runs discovery and analysis for every explicitly selected plugin. Writes nothing.</summary>
public static class DryRun
{
    public static IReadOnlyList<PluginAnalysis> Analyze(
        Settings settings,
        IReadOnlyList<ISkyrimModGetter> activeLoadOrder,
        ILinkCache<ISkyrimMod, ISkyrimModGetter> linkCache)
    {
        var errors = SettingsValidator.Validate(settings);
        if (errors.Count > 0)
            throw new InvalidOperationException("Invalid settings:" + Environment.NewLine + string.Join(Environment.NewLine, errors));

        var results = new List<PluginAnalysis>();
        foreach (var selection in settings.Plugins)
        {
            var mod = activeLoadOrder.FirstOrDefault(m => m.ModKey == selection.Plugin);
            if (mod is null)
            {
                results.Add(new PluginAnalysis
                {
                    Plugin = selection.Plugin,
                    Probability = selection.Probability,
                    Seed = settings.Seed,
                    Found = false,
                    Warnings = ["Plugin is not active in the load order; it was not analysed."],
                });
                continue;
            }

            var discovery = PlacementDiscovery.Discover(mod, activeLoadOrder, linkCache);
            results.Add(PluginAnalyzer.Analyze(
                selection.Plugin,
                discovery.Placements,
                discovery.OverridesOfOtherPlacements,
                selection.Probability,
                settings.Seed));
        }

        return results;
    }
}
