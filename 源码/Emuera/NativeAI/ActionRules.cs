#nullable enable
using System;
using System.Linq;
using System.Text.Json;

namespace MinorShift.Emuera.NativeAI;

internal static class ActionRules
{
    internal static string Suggestions(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() > 3) throw new ArgumentException("建议行动格式无效。");
        var suggestions = value.EnumerateArray().Select(v =>
        {
            if (v.ValueKind != JsonValueKind.String) throw new ArgumentException("建议行动必须为文字。");
            string text = v.GetString()!;
            InventoryState.Text(text, 80, true);
            if (text.Contains('\n') || text.Contains('\r')) throw new ArgumentException("建议行动应为一行文字。");
            return text.Trim();
        }).Distinct(StringComparer.Ordinal).ToArray();
        return JsonSerializer.Serialize(suggestions, InventoryState.Json);
    }
    internal static string[] SuggestionList(string snapshot, string[]? defaults = null)
    {
        if (string.IsNullOrWhiteSpace(snapshot) || snapshot == "[]") return defaults?.ToArray() ?? [];
        try
        {
            using var doc = JsonDocument.Parse(snapshot);
            string normalized = Suggestions(doc.RootElement);
            return JsonSerializer.Deserialize<string[]>(normalized)!;
        }
        catch (Exception e) when (e is JsonException or ArgumentException)
        { return defaults?.ToArray() ?? []; }
    }
    internal static int CommonAction(string text, ScenarioDefinition? scenario = null)
    {
        if (scenario == null) return 0;
        text = text.Trim().TrimEnd('。', '！', '!').Replace(" ", "").Replace("\t", "");
        if (text.Length > 120) return 0;
        foreach (var (key, actions) in scenario.Commands)
            foreach (string prefix in scenario.CommandPrefixes)
                foreach (string suffix in scenario.CommandSuffixes)
                    if (actions.Any(action => text == prefix + action + suffix)) return int.Parse(key);
        return 0;
    }
}
