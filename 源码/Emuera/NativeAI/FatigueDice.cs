#nullable enable
using System;

namespace MinorShift.Emuera.NativeAI;

// Resolving a die only prepares immutable outcome data. Fatigue is charged by the
// caller when the complete action is adopted, never by preview, animation or retry.
internal static class FatigueDice
{
    internal static int Effective(int rawRoll, int penalty, int sides = 20)
    {
        if (sides < 2 || rawRoll < 1 || rawRoll > sides || penalty < 0)
            throw new ArgumentException("骰点或疲劳减值无效。");
        return (int)Math.Clamp((long)rawRoll - penalty, 1, sides);
    }

    internal static string Explanation(int rawRoll, int effectiveRoll, int penalty, int sides = 20) =>
        $"D{sides}：原始 {rawRoll} − 疲劳 {penalty} = 有效 {effectiveRoll}";

    internal static ScenarioActionOutcome Resolve(InventoryState state, ScenarioDefinition scenario, string key, int rawRoll)
    {
        // The existing resolver retains its validation and behavior for packages
        // that have not enabled point-based fatigue.
        var original = scenario.Resolve(key, rawRoll);
        if (!scenario.FatigueRules.Enabled || !state.FatiguePoints.HasValue) return original;
        int penalty = FatigueSystem.Penalty(state, scenario);
        int effectiveRoll = Effective(rawRoll, penalty, original.Sides);
        var outcome = scenario.Resolve(key, effectiveRoll);
        string explanation = Explanation(rawRoll, effectiveRoll, penalty, original.Sides);
        string prefix = $"D{outcome.Sides}：{effectiveRoll}";
        string detail = outcome.Summary.StartsWith(prefix, StringComparison.Ordinal)
            ? outcome.Summary[prefix.Length..].TrimStart(' ', '/', '：', ':', '|') : outcome.Summary;
        return outcome with
        {
            RawRoll = rawRoll, FatiguePenalty = penalty, FatigueCost = FatigueSystem.ActionCost(scenario, key),
            Summary = explanation + (detail.Length > 0 ? " / " + detail : ""),
            Instruction = outcome.Instruction + "\n" + explanation +
                "。收益已经按有效骰点确定，只按此结果描写；不得再次扣减骰点或重掷。"
        };
    }
}
