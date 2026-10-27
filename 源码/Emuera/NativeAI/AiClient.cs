#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace MinorShift.Emuera.NativeAI;

internal sealed record AiRequest(string Prompt, string Player, string Scenario, string GameTime, int ReplyLimit, int MemoryLimit, string Style, GenerationSettings? Parameters = null, ModelLimits? Limits = null, InventoryExchange? Inventory = null);
internal sealed record AiResult(bool Success, string Text, string Warning = "", string InventorySnapshot = "", string InventoryChanges = "", string Suggestions = "[]", bool RequiresApproval = false, string Rationale = "");

internal sealed class AiClient(HttpClient http, ConversationStore memory, AccountUsageStore? usageStore = null)
{
    public async Task<AiResult> SendAsync(AiSettings settings, AiRequest request, CancellationToken cancellation)
    {
        settings.Validate();
        if (string.IsNullOrWhiteSpace(request.Player)) return new(false, "输入为空，请先输入对话内容。");
        string system = PromptBuilder.System(request);
        if (request.Inventory != null)
        {
            int count = Math.Min(2, request.Parameters?.HistoryTurns ?? (request.MemoryLimit > 0 ? 2 : 0));
            var recent = count == 0 ? new List<ConversationTurn>() : memory.Read(request.Scenario).TakeLast(count).ToList();
            request = request with { Prompt = PromptBuilder.User(request, recent) };
        }
        List<ConversationTurn> history;
        try { history = request.Parameters is { } parameters ? GenerationPolicy.History(memory, request, parameters, system) : memory.Context(request.Scenario, request.MemoryLimit); }
        catch (ArgumentException e) { return new(false, e.Message); }
        var messages = new List<object>
        {
            new { role = "system", content = system },
        };
        if (request.Inventory == null)
        {
            foreach (var turn in history)
            {
                messages.Add(new { role = "user", content = turn.Player });
                messages.Add(new { role = "assistant", content = turn.Reply });
            }
        }
        else request = request with { Prompt = PromptBuilder.WithHistory(request.Prompt, history) };
        messages.Add(new { role = "user", content = request.Prompt });
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(TimeSpan.FromSeconds(settings.TimeoutSeconds));
        using var message = new HttpRequestMessage(HttpMethod.Post, settings.BaseUrl.TrimEnd('/') + "/chat/completions");
        var key = settings.GetKey();
        if (!string.IsNullOrWhiteSpace(key)) message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        var payload = new Dictionary<string, object>
        {
            ["model"] = settings.Model.Trim(), ["messages"] = messages,
            ["max_tokens"] = Math.Max(1, request.ReplyLimit), ["temperature"] = 0.8, ["stream"] = false,
        };
        if (request.Parameters is { } generation) GenerationPolicy.Apply(payload, settings, generation);
        // Official DeepSeek structured turns use its documented JSON output mode.
        // https://api-docs.deepseek.com/guides/json_mode/
        // Other compatible providers are not assumed to accept this parameter.
        if (request.Inventory != null && ApiAccount.IsDeepSeek(settings))
            payload["response_format"] = new { type = "json_object" };
        // A 64-token connectivity probe should not spend its entire budget on reasoning.
        // https://api-docs.deepseek.com/zh-cn/guides/thinking_mode/
        if (request.Scenario == "connection_test" && ApiAccount.IsDeepSeek(settings) &&
            settings.Model.ToLowerInvariant() is "deepseek-flash" or "deepseek-v4-flash" or "deepseek-v4-flash-vision-exp" or "deepseek-v4-pro")
            payload["thinking"] = new { type = "disabled" };
        message.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
        try
        {
            using var response = await http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                // Do not expose server bodies: they may echo credentials or private input.
                return new(false, (int)response.StatusCode switch
                {
                    400 => "请求参数与模型不兼容，请检查所选模型，或将思考模式改为跟随模型并恢复均衡预设。",
                    401 or 403 => "认证失败，请检查 API 密钥与服务权限。",
                    404 => "接口或模型不存在，请检查服务地址与模型名称。",
                    429 => "服务限流或额度不足，请稍后再试并检查余额。",
                    _ => $"模型服务返回错误（状态码 {(int)response.StatusCode}），请检查服务状态。",
                });
            }
            var body = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                return new(false, "服务没有返回有效的文字回复，请确认接口兼容性。");
            var warning = "";
            try
            {
                var model = document.RootElement.TryGetProperty("model", out var modelValue) && modelValue.ValueKind == JsonValueKind.String ? modelValue.GetString() : settings.Model;
                usageStore?.Record(settings, TokenUsage.Read(document.RootElement), model ?? settings.Model);
            }
            catch (Exception e) when (e is System.IO.IOException or UnauthorizedAccessException or JsonException)
            { warning = "用量记录保存失败，今日统计可能不完整。"; }
            if (!document.RootElement.TryGetProperty("choices", out var choices) || choices.ValueKind != JsonValueKind.Array || choices.GetArrayLength() == 0 ||
                choices[0].ValueKind != JsonValueKind.Object || !choices[0].TryGetProperty("message", out var reply) || reply.ValueKind != JsonValueKind.Object ||
                !reply.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.String)
                return new(false, "服务没有返回有效的文字回复，请确认接口兼容性。");
            string rawText = content.GetString() ?? "";
            bool truncated = choices[0].TryGetProperty("finish_reason", out var finish) && finish.ValueKind == JsonValueKind.String && finish.GetString() == "length";
            if (truncated && request.Inventory != null)
                return new(false, "本轮回复达到生成预算，结算可能不完整，物品、人物状态和时间未变。请提高预算后重试。");
            if (string.IsNullOrWhiteSpace(rawText))
                return new(false, rawText.Length == 0
                    ? "模型服务返回了空内容，没有提供可采用的最终回复。本轮进度未变，请重试。"
                    : "模型服务只返回了空白字符，没有提供可采用的JSON剧情。本轮进度未变，请重试。");
            var text = rawText.Trim();
            // Only strip an actual leading reasoning block. A literal tag inside JSON/story is data.
            while (text.StartsWith("<think>", StringComparison.Ordinal))
            {
                int end = text.IndexOf("</think>", StringComparison.Ordinal);
                if (end < 0) return new(false, "模型只返回了不完整的思考片段，没有最终回复。本轮进度未变。");
                text = text[(end + 8)..].TrimStart();
            }
            if (string.IsNullOrWhiteSpace(text)) return new(false, "模型只返回了思考片段，没有最终回复。本轮进度未变。");
            string inventorySnapshot = "", inventoryChanges = "", suggestions = "[]", rationale = "";
            bool requiresApproval = false;
            if (request.Inventory != null)
            {
                if (truncated) return new(false, "本轮回复达到生成预算，物品或人物状态结算可能不完整，物品、人物状态和时间未变。请提高预算后重试。");
                try
                {
                    var inventoryReply = InventoryReply.Apply(request.Inventory, text);
                    text = inventoryReply.Story;
                    inventorySnapshot = inventoryReply.Snapshot;
                    inventoryChanges = inventoryReply.Changes;
                    suggestions = inventoryReply.Suggestions;
                    requiresApproval = inventoryReply.RequiresApproval;
                    rationale = inventoryReply.Rationale;
                    warning += inventoryReply.Warning;
                    if (request.Inventory.Action == null)
                    {
                        string review = ReplyConsistency.MissingItemOperations(request.Player, request.Inventory.Snapshot, inventorySnapshot);
                        string storyReview = ReplyConsistency.MissingStoryChanges(text, request.Inventory.Snapshot, inventorySnapshot);
                        if (storyReview.Length > 0) review += (review.Length > 0 ? "\n" : "") + storyReview;
                        if (review.Length > 0) { requiresApproval = true; warning += (warning.Length > 0 ? "\n" : "") + review; }
                    }
                }
                catch (InvalidOperationException e) { return new(false, e.Message); }
            }
            else if (truncated) warning += "回复已达到单次生成预算，内容可能尚未完整。";
            timeout.Token.ThrowIfCancellationRequested();
            // Inventory-bound turns are appended by ERB only after acceptance and commit.
            if (request.Scenario != "connection_test" && request.Inventory == null)
            {
                try { memory.Append(request.Scenario, new ConversationTurn(request.GameTime, request.Player, text)); }
                catch (Exception e) when (e is System.IO.IOException or UnauthorizedAccessException) { warning += "回复已生成，但记忆保存失败，请检查目录写入权限。"; }
            }
            return new(true, text, warning, inventorySnapshot, inventoryChanges, suggestions, requiresApproval, rationale);
        }
        catch (OperationCanceledException) { return new(false, cancellation.IsCancellationRequested ? "本次请求已取消。" : "模型回复超时，请检查服务或在设置中增加等待时间。"); }
        catch (HttpRequestException) { return new(false, "无法连接模型服务，请检查服务地址、网络以及本地模型是否已启动。"); }
        catch (JsonException) { return new(false, "模型服务返回了无法识别的数据，请确认接口兼容性。"); }
    }
}
