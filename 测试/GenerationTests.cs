using System.Net;
using System.Text;
using System.Text.Json;
using MinorShift.Emuera.NativeAI;

internal static class GenerationTests
{
    internal static async Task Run()
    {
        static void Check(bool value, string text)
        {
            if (!value) throw new Exception("生成参数测试失败：" + text);
            Console.WriteLine("通过：" + text);
        }
        var dir = Path.Combine(Path.GetTempPath(), "EraAI-generation-" + Guid.NewGuid().ToString("N"));
        var store = new GenerationSettingsStore(dir);
        var defaults = store.Load();
        Check(defaults.PresetName == "均衡" && defaults.OutputTokens == 0 && defaults.ContextTokens == 0 && defaults.Thinking == "off", "新参数使用独立的均衡默认值");
        Check(defaults.Edit(10, "long").Edit(1, "500").Edit(2, "4000").Edit(3, "4").Edit(8, "low").PresetName == "均衡",
            "修改长度、预算、历史和思考不再误标为自定义采样");
        Check(defaults.Edit(4, "0.6").PresetName == "自定义", "修改采样参数后显示自定义");
        var edited = defaults.Edit(1, "777").Edit(3, "2").Edit(4, "0.4").Edit(5, "0.9").Edit(6, "0.3").Edit(7, "0.2");
        store.Save(edited);
        Check(new GenerationSettingsStore(dir).Load() == edited && edited.PresetName == "自定义", "参数持久化，单项修改自动标记自定义");
        string before = File.ReadAllText(Path.Combine(dir, "generation-settings.json"));
        foreach (var (field, value) in new[] { (1, "-1"), (2, "777"), (3, "65"), (4, "NaN"), (4, "2.1"), (5, "0"), (6, "-0.1"), (7, "2.1"), (8, "unknown"), (4, "0,8") })
        {
            bool rejected = false;
            try { store.Save(store.Load().Edit(field, value)); } catch (ArgumentException) { rejected = true; }
            Check(rejected && File.ReadAllText(Path.Combine(dir, "generation-settings.json")) == before, "非法参数不覆盖原设置");
        }
        foreach (string name in new[] { "balanced", "steady", "creative" }) GenerationSettings.FromPreset(name).Validate();
        Check(GenerationSettings.FromPreset("creative").Temperature > GenerationSettings.FromPreset("steady").Temperature, "生成方案有实际不同的参数");
        var c1 = new AiSettings { Mode = "api", BaseUrl = "https://example.invalid/v1", Model = "one" };
        var c2 = c1 with { Model = "two" };
        var large = edited with { OutputTokens = 32768, ContextTokens = 262144, ResponseLength = "long", Thinking = "high" };
        store.Save(large, c1);
        Check(store.Load(c1) == large && store.Load(c2).OutputTokens == 0 && store.Load(c2).ContextTokens == 0,
            "每模型独立保存预算，新模型默认自动，不沿用其他模型的 Token 数");
        var preset = large.Edit(9, "steady");
        Check(preset.OutputTokens == 32768 && preset.ContextTokens == 262144 && preset.HistoryTurns == large.HistoryTurns &&
            preset.Thinking == "high" && preset.ResponseLength == "long", "预设只改采样，保留预算、历史、思考和表达长度");
        var automatic = new Dictionary<string, object> { ["max_tokens"] = 64, ["max_completion_tokens"] = 64 };
        GenerationPolicy.Apply(automatic, c1, defaults);
        Check(!automatic.ContainsKey("max_tokens") && !automatic.ContainsKey("max_completion_tokens"), "自动输出不发送固定 Token 预算");
        GenerationPolicy.Apply(automatic, c1, large);
        Check((int)automatic["max_tokens"] == 32768, "手动输出可以超过旧统一上限，原样发送");
        using var metadata = JsonDocument.Parse("""{"context_window":262144,"max_output_tokens":40000,"inputTokenLimit":200000}""");
        var limits = ModelLimits.Parse(metadata.RootElement, "one");
        limits.Validate(large);
        bool overLimit = false;
        try { limits.Validate(large with { OutputTokens = 40001 }); } catch (ArgumentException) { overLimit = true; }
        Check(overLimit && limits.Description.Contains("262144"), "只根据服务返回的当前模型容量校验");
        new ModelLimits("unknown").Validate(large);
        Check(new ModelLimits("unknown").Description.Contains("未获取"), "未知容量不编造模型限制");
        var reduced = new ModelLimits("one", 8192, 1024);
        foreach (int field in new[] { 3, 4, 8, 9, 10, 11 }) reduced.Validate(large, field);
        Check(true, "容量更新后旧预算超限，不阻止修改历史、采样、预设、回复长度和思考设置");
        reduced.Validate(large with { OutputTokens = 512 }, 1);
        reduced.Validate(large with { OutputTokens = 512, ContextTokens = 0 }, 2);
        bool fullValidation = false;
        try { reduced.Validate(large); } catch (ArgumentException) { fullValidation = true; }
        Check(fullValidation, "实际请求仍完整校验所有预算，不绕过模型容量限制");
        Check(defaults.ContextSummary(new("one", 1048576)) == "自动 / 容量 1,048,576 Token" &&
            defaults.ContextSummary(new("one", MaxInputTokens: 12345)).Contains("输入上限 12,345") &&
            defaults.ContextSummary(new("one")) == "自动 / 容量未获取", "主菜单容量摘要简洁、数值分组，并区分输入与总容量");
        var now = DateTimeOffset.UtcNow;
        var capabilities = new ModelLimitsStore(dir, () => now);
        capabilities.Save(c1, [limits]);
        Check(capabilities.Get(c1) == limits && capabilities.Get(c2).ContextWindow == null &&
            capabilities.Get(c1 with { BaseUrl = "https://other.invalid" }).ContextWindow == null, "模型容量缓存隔离模型与服务");
        now = now.AddDays(2);
        Check(capabilities.Get(c1).ContextWindow == null, "模型容量缓存过期后显示未获取，避免长期依赖旧限制");
        var legacyDir = Path.Combine(dir, "legacy"); Directory.CreateDirectory(legacyDir);
        File.WriteAllText(Path.Combine(legacyDir, "generation-settings.json"), """{"OutputTokens":1024,"ContextTokens":8192,"Temperature":0.6}""");
        var migrated = new GenerationSettingsStore(legacyDir).Load(c1);
        Check(migrated.OutputTokens == 0 && migrated.ContextTokens == 0 && migrated.Temperature == .6m,
            "旧统一预算迁移为自动，保留已有采样设置");
        var memory = new ConversationStore(Path.Combine(dir, "memory"));
        memory.Append("rain_room", new("第1天", new string('旧', 1000), "很久以前"));
        memory.Append("rain_room", new("第2天", "最近一句", "最近回应"));
        memory.Append("rain_room", new("第3天", "最新一句", "最新回应"));
        var connection = new AiSettings { Mode = "api", BaseUrl = "https://example.invalid/v1", Model = "test-model", ProtectedKey = SecretProtection.Protect("fake-generation-test-key") };
        var request = new AiRequest("角色设定与当前输入", "当前输入", "rain_room", "第4天", 64, 0, "", edited);
        var history = GenerationPolicy.History(memory, request, edited with { ContextTokens = 2048, OutputTokens = 512, HistoryTurns = 64 }, "系统规则");
        Check(history.Count == 2 && history[0].Player == "最近一句" && history[1].Player == "最新一句", "保留最近连续对话，超预算时移除较早的大段历史");
        Check(GenerationPolicy.History(memory, request, edited with { HistoryTurns = 0 }, "系统规则").Count == 0, "历史设为零不读取历史进入请求");
        Check(GenerationPolicy.History(memory, request, defaults with { HistoryTurns = 64 }, "规则").Count == 3,
            "未获取模型容量时自动模式只按历史轮数筛选，不套用固定上下文");
        var known = request with { Limits = new("test-model", 2048, 1024) };
        Check(GenerationPolicy.History(memory, known, defaults with { HistoryTurns = 64 }, "规则").Count == 2,
            "自动上下文按服务返回容量裁剪历史");
        int calls = 0;
        var http = new HttpClient(new FakeHandler(async (message, token) =>
        {
            calls++;
            using var doc = JsonDocument.Parse(await message.Content!.ReadAsStringAsync(token));
            var json = doc.RootElement;
            Check(json.GetProperty("max_tokens").GetInt32() == 777, "单次生成预算实际发送到兼容接口");
            Check(json.GetProperty("temperature").GetDecimal() == .4m && json.GetProperty("top_p").GetDecimal() == .9m &&
                json.GetProperty("frequency_penalty").GetDecimal() == .3m && json.GetProperty("presence_penalty").GetDecimal() == .2m, "四项采样参数实际进入请求");
            var messages = json.GetProperty("messages");
            Check(messages.GetArrayLength() == 6 && messages[1].GetProperty("content").GetString() == "最近一句", "历史轮数实际限制发送的对话");
            Check(messages[0].GetProperty("content").GetString()!.Contains("物品") && messages[0].GetProperty("content").GetString()!.Contains("长度适中"), "生成指令保留剧本状态约束");
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"choices\":[{\"finish_reason\":\"length\",\"message\":{\"content\":\"测试回复\"}}]}", Encoding.UTF8, "application/json") };
        }));
        var client = new AiClient(http, memory);
        var result = await client.SendAsync(connection, request, default);
        Check(result.Success && result.Warning.Contains("生成预算") && calls == 1, "达到输出上限提示可能截断，不自动重试");
        var oversized = request with { Prompt = new string('长', 4000), Parameters = edited with { ContextTokens = 2048 } };
        Check(!(await client.SendAsync(connection, oversized, default)).Success && calls == 1, "必需输入超预算时发送前拦截，不产生额外调用");

        foreach (var c in new[] {
            connection with { BaseUrl = "https://api.deepseek.com", Model = "deepseek-flash" },
            connection with { BaseUrl = "https://api.openai.com/v1", Model = "gpt-5.4" },
            connection with { BaseUrl = "https://generativelanguage.googleapis.com/v1beta/openai", Model = "gemini-3-flash" },
            connection with { BaseUrl = "https://api.x.ai/v1", Model = "grok-4.7" }, connection })
        foreach (string mode in new[] { "auto", "off", "low", "high" })
        {
            string notice = GenerationPolicy.Notice(c, defaults with { Thinking = mode }).ToLowerInvariant();
            Check(!new[] { "deepseek", "flash", "gpt", "gemini", "grok", "接口", "适配" }.Any(notice.Contains),
                "生成设置采用通用说明，不暴露厂商型号及接口细节");
        }
        static Dictionary<string, object> Payload() => new() { ["max_tokens"] = 64, ["temperature"] = .8m };
        var ds = connection with { BaseUrl = "https://api.deepseek.com", Model = "deepseek-flash" };
        var body = Payload(); GenerationPolicy.Apply(body, ds, edited with { Thinking = "off" });
        Check(body.ContainsKey("thinking") && body.ContainsKey("temperature") && !body.ContainsKey("top_p"), "DeepSeek Flash 关闭思考时不发送模型固定的核采样");
        body = Payload(); GenerationPolicy.Apply(body, ds, edited with { Thinking = "low", TopP = .5m });
        Check(!body.ContainsKey("temperature") && !body.ContainsKey("frequency_penalty") && (decimal)body["top_p"] == .95m && (string)body["reasoning_effort"] == "low", "DeepSeek 思考模式移除无效采样并调整核采样下限");
        body = Payload(); GenerationPolicy.Apply(body, ds with { Model = "deepseek-v4-pro" }, edited with { Thinking = "off" });
        Check(body.ContainsKey("top_p"), "DeepSeek Pro 关闭思考时保留可用核采样");
        body = Payload(); GenerationPolicy.Apply(body, connection with { BaseUrl = "https://api.openai.com/v1", Model = "gpt-5.4" }, edited);
        Check(body.ContainsKey("max_completion_tokens") && !body.ContainsKey("max_tokens") && !body.ContainsKey("temperature") && (string)body["reasoning_effort"] == "low", "GPT 思考模型使用兼容的输出字段和低强度回退");
        body = Payload(); GenerationPolicy.Apply(body, connection with { BaseUrl = "https://api.openai.com/v1", Model = "gpt-5-pro" }, edited);
        Check(!body.ContainsKey("reasoning_effort"), "GPT Pro 型号不强行指定可能不支持的低思考强度");
        body = Payload(); GenerationPolicy.Apply(body, connection with { BaseUrl = "https://generativelanguage.googleapis.com/v1beta/openai", Model = "gemini-3-flash" }, edited);
        Check((string)body["reasoning_effort"] == "low" && !body.ContainsKey("frequency_penalty"), "Gemini 无法关闭的型号降为低强度并省略未适配惩罚");
        body = Payload(); GenerationPolicy.Apply(body, connection with { BaseUrl = "https://api.x.ai/v1", Model = "grok-4.7" }, edited);
        Check((string)body["reasoning_effort"] == "low" && !body.ContainsKey("presence_penalty"), "Grok 思考模型不发送会报错的惩罚参数");
        body = Payload(); GenerationPolicy.Apply(body, connection, edited with { Thinking = "high" });
        Check(!body.ContainsKey("thinking") && !body.ContainsKey("reasoning_effort"), "未知服务不强行发送模型专属思考字段");
    }
}
