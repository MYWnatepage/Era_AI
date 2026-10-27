#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;

namespace MinorShift.Emuera.NativeAI;

// Read-only, location-specific action labels for map details. Common actions stay
// in the action menu, which separately checks day/time and daily limits.
internal static class SceneActionPresentation
{
    internal static string[] ForScene(InventoryState state, ScenarioDefinition? scenario, SceneCard card)
    {
        if (scenario == null) return [];
        // These helpers inspect location and adjacency only. Reuse the read-only scene
        // list rather than serializing a complete save for every map label.
        var context = new InventoryState { KnownScenes = state.KnownScenes,
            Scene = new SceneState { Location = card.Name, PresentCharacters = [], Summary = "" } };
        var result = new List<string>();
        foreach (var entry in scenario.LocationActions)
            if (ScenarioActions.LocationReason(context, scenario, entry.Key).Length == 0) result.Add("[" + entry.Value.Label + "]");
        if (scenario.LocationActions.Count == 0 && scenario.ActionLocations.ContainsKey("hunt") &&
            ScenarioActions.LocationReason(context, scenario, "hunt").Length == 0) result.Add("[狩猎]");
        return result.ToArray();
    }

    internal static string[] BenefitsForScene(InventoryState state, ScenarioDefinition? scenario, SceneCard card)
    {
        if (scenario == null) return [];
        var template = scenario.World.Scenes.FirstOrDefault(scene => scene.Name == card.Name || scene.Aliases.Contains(card.Name));
        if (template == null) return [];
        var context = new InventoryState { KnownScenes = state.KnownScenes,
            Scene = new SceneState { Location = card.Name, PresentCharacters = [], Summary = "" } };
        var result = new List<string>();
        foreach (var (key, name) in new[] { ("rest", "休息"), ("sleep", "入眠") })
        {
            if (ScenarioActions.LocationReason(context, scenario, key).Length > 0) continue;
            if (scenario.FatigueRules.Enabled)
            {
                decimal points = (key == "rest" ? scenario.FatigueRules.RestLocationBonus : scenario.FatigueRules.SleepLocationBonus)
                    .GetValueOrDefault(template.Id);
                if (points > 0) result.Add($"[{name}：疲劳恢复 +{points:0.###} 点]");
                continue;
            }
            if (!scenario.RecoveryRules.TryGetValue(key, out var rule)) continue;
            int bonus = rule.LocationBonus.GetValueOrDefault(template.Id);
            if (bonus > 0) result.Add($"[{name}：{rule.Label} +{bonus} 级]");
        }
        return result.ToArray();
    }
}
