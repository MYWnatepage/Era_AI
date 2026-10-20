#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace MinorShift.Emuera.NativeAI;

internal static class GenerationPolicy
{
    internal static string Host(AiSettings connection) => new Uri(connection.BaseUrl).Host.ToLowerInvariant();
    private static bool Ds(AiSettings c) => ApiAccount.IsDeepSeek(c) && c.Model.ToLowerInvariant() is "deepseek-flash" or "deepseek-v4-flash" or "deepseek-v4-flash-vision-exp" or "deepseek-v4-pro";
    private static bool OpenAiReasoning(AiSettings c) => Host(c) == "api.openai.com" && !c.Model.Contains("chat", StringComparison.OrdinalIgnoreCase) &&
        (c.Model.StartsWith("gpt-5", StringComparison.OrdinalIgnoreCase) || c.Model.StartsWith("gpt-6", StringComparison.OrdinalIgnoreCase) ||
         c.Model.StartsWith("o1", StringComparison.OrdinalIgnoreCase) || c.Model.StartsWith("o3", StringComparison.OrdinalIgnoreCase) || c.Model.StartsWith("o4", StringComparison.OrdinalIgnoreCase));
    private static bool GrokReasoning(AiSettings c) => Host(c) == "api.x.ai" && !c.Model.Contains("non-reasoning", StringComparison.OrdinalIgnoreCase) &&
        (c.Model.StartsWith("grok-4", StringComparison.OrdinalIgnoreCase) || c.Model.Contains("reasoning", StringComparison.OrdinalIgnoreCase) || c.Model.StartsWith("grok-3-mini", StringComparison.OrdinalIgnoreCase));

    internal static string Notice(AiSettings c, GenerationSettings p)
    {
        if (Ds(c)) return p.Thinking == "off"
            ? (c.Model.Contains("flash", StringComparison.OrdinalIgnoreCase) ? "当前请求优先关闭思考；核采样由模型决定，随机程度、重复抑制和话题扩展按设置使用。" : "当前请求优先关闭思考，四项采样参数按设置使用。")
            : "当前思考模式下，随机程度、重复抑制和话题扩展由模型决定；核采样可能自动调整。";
        if (OpenAiReasoning(c)) return c.Model.Contains("-pro", StringComparison.OrdinalIgnoreCase)
            ? "当前思考强度与采样参数由模型决定，页面设置会保留。"
            : "当前采样参数由模型决定；关闭优先会尝试低强度，部分模型无法完全关闭思考。";
        if (Host(c) == "generativelanguage.googleapis.com") return "思考强度在支持时使用；无法关闭时尝试低强度，重复抑制与话题扩展暂不使用。";
        if (GrokReasoning(c)) return "当前采样参数由模型决定；思考强度在支持时使用，无法关闭时尝试低强度。";
        return "按设置请求使用采样参数，实际支持情况由模型决定；无法控制思考时使用模型默认。";
    }

    internal static string SamplingStatus(AiSettings c, GenerationSettings p, bool detail = false)
    {
        // Probe the existing request adapter without sending a request. Non-default values
        // distinguish unsupported controls from controls omitted because they are neutral.
        var body = new Dictionary<string, object>();
        Apply(body, c, p with { TopP = .9m, FrequencyPenalty = .1m, PresencePenalty = .1m });
        string Status(string key) => body.ContainsKey(key) ? "请求使用" : "模型决定";
        int enabled = new[] { "temperature", "top_p", "frequency_penalty", "presence_penalty" }.Count(body.ContainsKey);
        if (!detail) return enabled == 0 ? "模型决定" : enabled == 4 ? "按设置请求使用" : "部分由模型决定";
        string top = body.TryGetValue("top_p", out var value) && Convert.ToDecimal(value) != .9m ? "自动调整" : Status("top_p");
        return "随机程度：" + Status("temperature") + " / 核采样：" + top + " / 重复抑制：" + Status("frequency_penalty") + " / 话题扩展：" + Status("presence_penalty");
    }
    internal static void Apply(Dictionary<string, object> payload, AiSettings c, GenerationSettings p)
    {
        p.Validate();
        payload.Remove("max_tokens");
        payload.Remove("max_completion_tokens");
        payload.Remove("temperature");
        if (p.OutputTokens > 0) payload[Host(c) == "api.openai.com" ? "max_completion_tokens" : "max_tokens"] = p.OutputTokens;
        string model = c.Model.ToLowerInvariant(), thinking = p.Thinking;
        bool sampling = true, penalties = true, topP = true;
        if (Ds(c))
        {
            bool active = thinking != "off";
            if (thinking != "auto") payload["thinking"] = new { type = active ? "enabled" : "disabled" };
            if (active && thinking != "auto") payload["reasoning_effort"] = thinking == "low" ? "low" : "high";
            sampling = penalties = !active;
            topP = model.Contains("flash") ? active : !active;
            if (topP && active) payload["top_p"] = Math.Max(.95m, p.TopP);
        }
        else if (OpenAiReasoning(c))
        {
            sampling = penalties = topP = false;
            if (thinking != "auto" && !model.Contains("-pro")) payload["reasoning_effort"] = thinking == "off" ? "low" : thinking;
        }
        else if (Host(c) == "generativelanguage.googleapis.com")
        {
            penalties = false;
            if (thinking != "auto" && (model.StartsWith("gemini-2.5") || model.StartsWith("gemini-3") || model.StartsWith("gemini-4")))
                payload["reasoning_effort"] = thinking == "off" ? (model.StartsWith("gemini-2.5") && !model.Contains("pro") ? "none" : "low") : thinking;
        }
        else if (GrokReasoning(c))
        {
            sampling = penalties = topP = false;
            if (thinking != "auto" && (model.StartsWith("grok-4.5") || model.StartsWith("grok-4.6") || model.StartsWith("grok-4.7") || model.StartsWith("grok-3-mini")))
                payload["reasoning_effort"] = thinking == "off" ? "low" : model.StartsWith("grok-3-mini") && thinking == "medium" ? "high" : thinking;
        }
        if (sampling) payload["temperature"] = p.Temperature;
        if (topP && !payload.ContainsKey("top_p") && p.TopP != 1m) payload["top_p"] = p.TopP;
        if (penalties && p.FrequencyPenalty != 0) payload["frequency_penalty"] = p.FrequencyPenalty;
        if (penalties && p.PresencePenalty != 0) payload["presence_penalty"] = p.PresencePenalty;
    }

    // Conservatively budget UTF-8 bytes plus message overhead. This is an estimate,
    // not a model tokenizer. Reserve explicitly configured output and keep mandatory instructions.
    internal static int Estimate(string text) => checked(Encoding.UTF8.GetByteCount(text) + 12);
    internal static List<ConversationTurn> History(ConversationStore memory, AiRequest request, GenerationSettings p, string system)
    {
        var limits = request.Limits ?? new ModelLimits("");
        limits.Validate(p);
        long context = p.ContextTokens > 0 ? p.ContextTokens : limits.ContextWindow ?? long.MaxValue;
        // Unknown server defaults are not guessed. Explicit output budgets reserve space.
        long inputBudget = context == long.MaxValue ? long.MaxValue : context - p.OutputTokens;
        if (limits.MaxInputTokens is long maxInput) inputBudget = Math.Min(inputBudget, maxInput);
        long remaining = inputBudget - 128 - Estimate(system) - Estimate(request.Prompt);
        if (remaining < 0) throw new ArgumentException("当前输入与角色说明超过上下文预算，请缩短输入、调大上下文或减少生成预算。尚未发出请求。");
        if (p.HistoryTurns == 0) return [];
        var selected = new List<ConversationTurn>();
        foreach (var turn in memory.Read(request.Scenario).TakeLast(p.HistoryTurns).Reverse())
        {
            long size = (long)Estimate(turn.Player) + Estimate(turn.Reply);
            if (size > remaining) break;
            selected.Add(turn);
            remaining -= size;
        }
        selected.Reverse();
        return selected;
    }
}
