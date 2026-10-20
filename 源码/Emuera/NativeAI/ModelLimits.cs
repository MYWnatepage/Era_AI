#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace MinorShift.Emuera.NativeAI;

internal sealed record ModelLimits(string Model, long? ContextWindow = null, long? MaxOutputTokens = null, long? MaxInputTokens = null)
{
    internal static ModelLimits Parse(JsonElement item, string id)
    {
        static long? Read(JsonElement obj, params string[] names)
        {
            foreach (string name in names)
                if (obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(name, out var value) &&
                    value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number) && number > 0) return number;
            return null;
        }
        var context = Read(item, "context_window", "context_length", "max_context_length");
        var output = Read(item, "max_output_tokens", "max_completion_tokens", "outputTokenLimit");
        var input = Read(item, "max_input_tokens", "inputTokenLimit");
        if (item.TryGetProperty("top_provider", out var provider))
        {
            context ??= Read(provider, "context_length", "context_window");
            output ??= Read(provider, "max_completion_tokens", "max_output_tokens");
        }
        return new(id, context, output, input);
    }
    internal void Validate(GenerationSettings p, int editedField = 0)
    {
        p.Validate();
        if ((editedField is 0 or 1) && MaxOutputTokens is long output && p.OutputTokens > output)
            throw new ArgumentException($"当前模型服务声明最大输出为 {output} Token，请调小生成预算或改为自动。");
        if ((editedField is 0 or 2) && ContextWindow is long context && p.ContextTokens > context)
            throw new ArgumentException($"当前模型服务声明上下文容量为 {context} Token，请调小上下文预算或改为自动。");
        long? availableContext = p.ContextTokens > 0 ? p.ContextTokens : ContextWindow;
        if ((editedField is 0 or 1 or 2) && availableContext is long total && p.OutputTokens >= total)
            throw new ArgumentException("生成预算占满了上下文，请调小生成预算或调大可用上下文。");
    }
    internal string Description => $"上下文容量：{Format(ContextWindow)} / 输入限制：{Format(MaxInputTokens)} / 最大输出：{Format(MaxOutputTokens)}";
    private static string Format(long? value) => value is long n ? n.ToString(CultureInfo.InvariantCulture) + " Token（服务返回）" : "未获取";
}

internal sealed record ModelLimitsCache(DateTimeOffset Updated, ModelLimits[] Models);
internal sealed class ModelLimitsStore(string directory, Func<DateTimeOffset>? clock = null)
{
    private readonly string path = Path.Combine(directory, "model-capabilities.json");
    internal static string ServiceId(AiSettings c) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(c.Mode + "\n" + c.BaseUrl.TrimEnd('/'))));
    private Dictionary<string, ModelLimitsCache> Read()
    {
        if (!File.Exists(path)) return [];
        try { return JsonSerializer.Deserialize<Dictionary<string, ModelLimitsCache>>(File.ReadAllText(path)) ?? []; }
        catch (JsonException) { return []; }
    }
    internal ModelLimits Get(AiSettings c)
    {
        var caches = Read();
        if (caches.TryGetValue(ServiceId(c), out var cache) && cache != null && cache.Models != null &&
            (clock?.Invoke() ?? DateTimeOffset.UtcNow) >= cache.Updated &&
            (clock?.Invoke() ?? DateTimeOffset.UtcNow) - cache.Updated < TimeSpan.FromDays(1))
            return cache.Models.FirstOrDefault(x => x != null && x.Model == c.Model) ?? new(c.Model);
        return new(c.Model);
    }
    internal string Status(AiSettings c)
    {
        var caches = Read();
        if (!caches.TryGetValue(ServiceId(c), out var cache) || cache == null || cache.Models == null) return "尚未查询，可刷新容量信息";
        var now = clock?.Invoke() ?? DateTimeOffset.UtcNow;
        if (now < cache.Updated || now - cache.Updated >= TimeSpan.FromDays(1)) return "缓存已过期，请刷新容量信息";
        var model = cache.Models.FirstOrDefault(x => x != null && x.Model == c.Model);
        if (model == null) return "列表未包含当前模型，请重新选择模型";
        if (model.ContextWindow == null && model.MaxOutputTokens == null && model.MaxInputTokens == null) return "已查询，当前模型未提供容量信息";
        return "已查询，未获取的项目表示未单独提供（" + cache.Updated.ToOffset(TimeSpan.FromHours(8)).ToString("MM-dd HH:mm") + " 更新）";
    }
    internal void Save(AiSettings c, ModelLimits[] models)
    {
        var caches = Read();
        caches[ServiceId(c)] = new(clock?.Invoke() ?? DateTimeOffset.UtcNow, models);
        Directory.CreateDirectory(directory);
        File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(caches, new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
        File.Move(path + ".tmp", path, true);
    }
}
