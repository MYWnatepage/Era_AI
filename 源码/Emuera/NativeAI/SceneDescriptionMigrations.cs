#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;

namespace MinorShift.Emuera.NativeAI;

internal sealed class SceneDescriptionMigration
{
    [JsonRequired] public string SceneName { get; set; } = "";
    [JsonRequired] public List<string> OldDescriptions { get; set; } = [];
    public List<string> OldRules { get; set; } = [];
}

// Authored legacy text belongs to a scenario package, not to the framework.
// Only exact, known initial descriptions are eligible; changed story prose wins.
internal static class SceneDescriptionMigrations
{
    internal static void Validate(SceneCatalog catalog)
    {
        var rules = catalog.DescriptionMigrations;
        if (rules == null || rules.Count > 192 || rules.Any(rule => rule == null))
            throw new ArgumentException("场景说明迁移表无效。");
        var unique = new HashSet<(string Scene, string Description)>();
        var uniqueRules = new HashSet<(string Scene, string Rule)>();
        foreach (var rule in rules)
        {
            InventoryState.Text(rule.SceneName, 120, true);
            if (rule.OldDescriptions == null || rule.OldRules == null || rule.OldDescriptions.Count > 16 || rule.OldRules.Count > 16 ||
                rule.OldDescriptions.Count + rule.OldRules.Count == 0 ||
                rule.OldDescriptions.Distinct(StringComparer.Ordinal).Count() != rule.OldDescriptions.Count ||
                rule.OldRules.Distinct(StringComparer.Ordinal).Count() != rule.OldRules.Count)
                throw new ArgumentException("场景说明迁移表的旧说明为空或重复。");
            var templates = catalog.Scenes.Where(scene => Matches(scene, rule.SceneName)).ToArray();
            if (templates.Length != 1) throw new ArgumentException("场景说明迁移表未对应唯一的初始地点。");
            foreach (string description in rule.OldDescriptions)
            {
                InventoryState.Text(description, 1200, true);
                if (!unique.Add((templates[0].Name, description)))
                    throw new ArgumentException("场景说明迁移表包含重复的地点旧说明。");
            }
            foreach (string text in rule.OldRules)
            {
                InventoryState.Text(text, 600, true);
                if (!uniqueRules.Add((templates[0].Name, text))) throw new ArgumentException("场景说明迁移表包含重复的地点旧规则。");
            }
        }
    }

    internal static bool Apply(InventoryState state, SceneCatalog? initial)
    {
        ArgumentNullException.ThrowIfNull(state);
        bool changed = SceneNarration.CleanLegacy(state);
        if (initial == null || initial.DescriptionMigrations.Count == 0) return changed;
        foreach (var card in state.KnownScenes)
        {
            var templates = initial.Scenes.Where(template => Matches(template, card.Name)).ToArray();
            if (templates.Length != 1) continue;
            var template = templates[0];
            bool legacy = initial.DescriptionMigrations.Any(rule => Matches(template, rule.SceneName) &&
                rule.OldDescriptions.Contains(card.Description, StringComparer.Ordinal));
            if (legacy && card.Description != template.Description)
            { card.Description = template.Description; changed = true; }
            bool oldRule = initial.DescriptionMigrations.Any(rule => Matches(template, rule.SceneName) &&
                rule.OldRules.Contains(card.Rules, StringComparer.Ordinal));
            if (oldRule && card.Rules != template.Rules) { card.Rules = template.Rules; changed = true; }
        }
        return changed;
    }

    private static bool Matches(SceneCard card, string name) =>
        string.Equals(card.Name, name, StringComparison.Ordinal) || card.Aliases.Contains(name, StringComparer.Ordinal);
}

// Short local narration must describe the world, never the renderer or save machinery.
internal static class SceneNarration
{
    internal static string Arrival(string location) => location == "野外"
        ? "你在野外停下脚步，打量着四周。" : "你来到" + location + "，在这里停下脚步。";
    internal static string Rest(string location, bool sleep) => sleep
        ? "你在" + location + "安顿下来，渐渐进入梦乡。"
        : "你在" + location + "歇了片刻，呼吸渐渐平稳。";

    internal static bool CleanLegacy(InventoryState state)
    {
        bool changed = false;
        string Clean(string text, string location)
        {
            // Match only old generated templates; custom story prose stays verbatim.
            if (text == "主角沿绘制的路线抵达这里，停留在具体坐标处。") return Arrival(location);
            if (text == "主角在原地入眠，结束当天活动。") return Rest(location, true);
            if (text == "主角在原地短暂休息，其他人物所在地保持不变。") return Rest(location, false);
            if (text == "该地点已在本局被提及，详细环境尚待剧情确认。") return "这里的环境还不熟悉。";
            if (text.StartsWith("本次程序行动结束，已到达" + location + "。当前在场：", StringComparison.Ordinal) &&
                text.EndsWith("。其他人物所在地不变。", StringComparison.Ordinal)) return Arrival(location);
            return text;
        }
        if (state.Scene is { } scene)
        {
            string text = Clean(scene.Summary, scene.Location);
            if (text != scene.Summary) { scene.Summary = text; changed = true; }
        }
        foreach (var card in state.KnownScenes)
        {
            string text = Clean(card.Description, card.Name);
            if (text != card.Description) { card.Description = text; changed = true; }
        }
        return changed;
    }
}
