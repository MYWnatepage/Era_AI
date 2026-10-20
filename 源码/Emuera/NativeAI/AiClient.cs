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

internal sealed record AiRequest(string Prompt, string Player, string Scenario, string GameTime, int ReplyLimit, int MemoryLimit, string Style, GenerationSettings? Parameters = null, ModelLimits? Limits = null);
internal sealed record AiResult(bool Success, string Text, string Warning = "");

internal sealed class AiClient(HttpClient http, ConversationStore memory, AccountUsageStore? usageStore = null)
{
    public async Task<AiResult> SendAsync(AiSettings settings, AiRequest request, CancellationToken cancellation)
    {
        settings.Validate();
        if (string.IsNullOrWhiteSpace(request.Player)) return new(false, "输入为空，请先输入对话内容。");
        string system = "你正在为中文文字冒险游戏扮演角色。只使用简体中文，不使用日文或日语假名。只输出最终角色回复，不输出推理过程、系统说明或标题。\n" + request.Style;
        if (request.Parameters != null)
            system += "\n以当前剧本提供的状态为准。可以自由组织台词和动作，但不要擅自完成任务、增减物品、改变数值或揭示未提供的秘密。\n" + request.Parameters.LengthInstruction;
        List<ConversationTurn> history;
        try { history = request.Parameters is { } parameters ? GenerationPolicy.History(memory, request, parameters, system) : memory.Context(request.Scenario, request.MemoryLimit); }
        catch (ArgumentException e) { return new(false, e.Message); }
        var messages = new List<object>
        {
            new { role = "system", content = system },
        };
        foreach (var turn in history)
        {
            messages.Add(new { role = "user", content = turn.Player });
            messages.Add(new { role = "assistant", content = turn.Reply });
        }
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
            var text = (content.GetString() ?? "").Trim();
            // Some local models put reasoning in the content field rather than a separate field.
            text = System.Text.RegularExpressions.Regex.Replace(text, @"<think>.*?</think>", "", System.Text.RegularExpressions.RegexOptions.Singleline).Trim();
            if (string.IsNullOrWhiteSpace(text) || text.Contains("<think>")) return new(false, "模型未生成可显示的最终回复，请调大生成预算，或将思考模式改为关闭优先、低强度。");
            if (choices[0].TryGetProperty("finish_reason", out var finish) && finish.ValueKind == JsonValueKind.String && finish.GetString() == "length")
                warning += "回复已达到单次生成预算，内容可能尚未完整。";
            if (request.Scenario != "connection_test")
            {
                try { memory.Append(request.Scenario, new ConversationTurn(request.GameTime, request.Player, text)); }
                catch (Exception e) when (e is System.IO.IOException or UnauthorizedAccessException) { warning += "回复已生成，但记忆保存失败，请检查目录写入权限。"; }
            }
            return new(true, text, warning);
        }
        catch (OperationCanceledException) { return new(false, cancellation.IsCancellationRequested ? "本次请求已取消。" : "模型回复超时，请检查服务或在设置中增加等待时间。"); }
        catch (HttpRequestException) { return new(false, "无法连接模型服务，请检查服务地址、网络以及本地模型是否已启动。"); }
        catch (JsonException) { return new(false, "模型服务返回了无法识别的数据，请确认接口兼容性。"); }
    }
}
