#nullable enable
using System;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;

namespace MinorShift.Emuera.NativeAI;

internal sealed record GenerationSettings
{
    public string Preset { get; init; } = "balanced";
    // Zero means automatic; no invented common model limit.
    public int OutputTokens { get; init; }
    public int ContextTokens { get; init; }
    public string ResponseLength { get; init; } = "medium";
    public int HistoryTurns { get; init; } = 12;
    public decimal Temperature { get; init; } = .8m;
    public decimal TopP { get; init; } = 1m;
    public decimal FrequencyPenalty { get; init; }
    public decimal PresencePenalty { get; init; }
    public string Thinking { get; init; } = "off";
    public string PresetName => Preset switch { "balanced" => "均衡", "steady" => "偏稳定", "creative" => "偏自由", _ => "自定义" };
    public string ThinkingName => Thinking switch { "auto" => "跟随模型", "off" => "关闭优先", "low" => "低", "medium" => "中", _ => "高" };
    public string LengthName => ResponseLength switch { "short" => "简短", "long" => "详细", _ => "适中" };
    internal string LengthInstruction => ResponseLength switch
    {
        "short" => "本次请简短回应，以一小段自然对话为主，避免反复铺陈。",
        "long" => "本次可详细回应，适当展开动作和氛围，内容完整后自然结束，避免重复凑字。",
        _ => "本次回复长度适中，以几段自然的角色对话和必要动作为主，内容完整后自然结束。",
    };
    internal string OutputText => OutputTokens == 0 ? "自动（模型默认）" : OutputTokens + " Token（手动）";
    internal string ContextText(ModelLimits limits) => ContextTokens > 0 ? ContextTokens + " Token（手动）" :
        limits.ContextWindow is long total ? "自动（上下文容量 " + total + " Token）" :
        limits.MaxInputTokens is long input ? "自动（输入容量 " + input + " Token）" : "自动（未获取模型限制，按历史轮数）";
    internal string ContextSummary(ModelLimits limits)
    {
        string Count(long n) => n.ToString("N0", CultureInfo.InvariantCulture);
        if (ContextTokens > 0) return "手动 " + Count(ContextTokens) + " Token";
        if (limits.ContextWindow is long total) return "自动 / 容量 " + Count(total) + " Token";
        if (limits.MaxInputTokens is long input) return "自动 / 输入上限 " + Count(input) + " Token";
        return "自动 / 容量未获取";
    }
    public void Validate()
    {
        if (OutputTokens < 0 || ContextTokens < 0) throw new ArgumentException("Token 预算请输入非负整数，0 表示自动，具体限制由模型服务决定。");
        if (ContextTokens > 0 && OutputTokens >= ContextTokens) throw new ArgumentException("生成预算需要小于上下文总预算，请为输入保留空间。");
        if (ResponseLength is not ("short" or "medium" or "long")) throw new ArgumentException("请选择简短、适中或详细。");
        if (HistoryTurns is < 0 or > 64) throw new ArgumentException("历史轮数范围为 0～64，0 表示不发送历史。");
        if (Temperature is < 0 or > 2 || TopP is <= 0 or > 1 || FrequencyPenalty is < 0 or > 2 || PresencePenalty is < 0 or > 2)
            throw new ArgumentException("随机程度、重复抑制和话题扩展范围为 0～2，核采样范围为大于 0 且不超过 1。");
        if (Thinking is not ("auto" or "off" or "low" or "medium" or "high")) throw new ArgumentException("请选择页面上的思考模式。");
    }
    public static GenerationSettings FromPreset(string preset) => preset switch
    {
        "balanced" => new(),
        "steady" => new() { Preset = "steady", Temperature = .4m },
        "creative" => new() { Preset = "creative", Temperature = 1.1m, FrequencyPenalty = .15m, PresencePenalty = .1m },
        _ => throw new ArgumentException("无效的生成预设。"),
    };
    internal GenerationSettings Edit(int field, string input)
    {
        input = input.Trim();
        int Integer() => int.TryParse(input, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : throw new ArgumentException("请输入整数，留空可取消。");
        decimal Number() => decimal.TryParse(input, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var value) ? value : throw new ArgumentException("请输入有效数值，小数使用英文句点，例如 0.8。");
        var next = field is >= 4 and <= 7 ? this with { Preset = "custom" } : this;
        next = field switch
        {
            1 => next with { OutputTokens = Integer() }, 2 => next with { ContextTokens = Integer() },
            3 => next with { HistoryTurns = Integer() }, 4 => next with { Temperature = Number() },
            5 => next with { TopP = Number() }, 6 => next with { FrequencyPenalty = Number() },
            7 => next with { PresencePenalty = Number() }, 8 => next with { Thinking = input },
            9 => FromPreset(input) with { OutputTokens = OutputTokens, ContextTokens = ContextTokens, HistoryTurns = HistoryTurns, Thinking = Thinking, ResponseLength = ResponseLength },
            10 => next with { ResponseLength = input },
            11 => next with { Preset = "custom" },
            _ => throw new ArgumentException("无效的生成参数。"),
        };
        next.Validate();
        return next;
    }
}

internal sealed record GenerationProfiles
{
    public int Version { get; init; } = 2;
    public GenerationSettings Defaults { get; init; } = new();
    public System.Collections.Generic.Dictionary<string, GenerationSettings> Profiles { get; init; } = [];
}
internal sealed class GenerationSettingsStore(string directory)
{
    private readonly string path = Path.Combine(directory, "generation-settings.json");
    private static string Id(AiSettings c) => ModelLimitsStore.ServiceId(c) + ":" + c.Model;
    private GenerationProfiles Read()
    {
        if (!File.Exists(path)) return new();
        try
        {
            string json = File.ReadAllText(path);
            using var document = JsonDocument.Parse(json);
            GenerationProfiles result;
            if (document.RootElement.TryGetProperty("Version", out _))
                result = JsonSerializer.Deserialize<GenerationProfiles>(json) ?? throw new JsonException();
            else
            {
                // Migrate the old uniform budgets to automatic without losing sampling choices.
                var old = JsonSerializer.Deserialize<GenerationSettings>(json) ?? throw new JsonException();
                result = new() { Defaults = old with { OutputTokens = 0, ContextTokens = 0 } };
            }
            if (result.Version != 2 || result.Defaults == null || result.Profiles == null) throw new JsonException();
            result.Defaults.Validate();
            foreach (var value in result.Profiles.Values) { if (value == null) throw new JsonException(); value.Validate(); }
            return result;
        }
        catch (Exception e) when (e is JsonException or ArgumentException or InvalidOperationException)
        { throw new InvalidOperationException("生成参数配置损坏，请备份后移走 generation-settings.json，再重新设置。"); }
    }
    internal GenerationSettings Load() => Read().Defaults;
    internal GenerationSettings Load(AiSettings connection)
    {
        var doc = Read();
        return doc.Profiles.TryGetValue(Id(connection), out var value) ? value : doc.Defaults with { OutputTokens = 0, ContextTokens = 0 };
    }
    internal void Save(GenerationSettings settings) => Write(Read() with { Defaults = settings });
    internal void Save(GenerationSettings settings, AiSettings connection)
    {
        settings.Validate();
        var doc = Read();
        doc.Profiles[Id(connection)] = settings;
        Write(doc);
    }
    private void Write(GenerationProfiles doc)
    {
        doc.Defaults.Validate();
        Directory.CreateDirectory(directory);
        File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(doc, new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
        File.Move(path + ".tmp", path, true);
    }
}
