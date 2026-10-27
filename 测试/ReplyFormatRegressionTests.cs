using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MinorShift.Emuera.NativeAI;

internal static class ReplyFormatRegressionTests
{
    private static void Check(bool value, string description)
    { if (!value) throw new Exception(description); Console.WriteLine("通过：" + description); }

    internal static async Task Run()
    {
        var state = new InventoryState();
        state.BindCharacters(new Dictionary<string, string> { ["player"] = "主角" });
        state.AddInitial(1, "食材", "饭食储备", "消耗品", "", 10, "player", "");
        var exchange = new InventoryExchange(state.Snapshot(), "actual-reply-fixture");
        var fixture = JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "ai-attributes-object.json")))!;
        fixture["turn_id"] = exchange.TurnId; fixture["base_revision"] = 0;
        string reply = fixture.ToJsonString();
        var applied = InventoryReply.Apply(exchange, reply);
        var next = InventoryState.Read(applied.Snapshot);
        Check(next.Item(2).Attributes == "材质：亚麻；状态：干净" && next.Quantity(2) == 2 && next.Item(2).Name == "干净布条",
            "真实失败回复中的简单属性对象可无损整理为文字，正常创建两条布条");
        Check(applied.Story == fixture["story"]!.GetValue<string>() && state.Items.Count == 1 && next.Revision == 1,
            "属性兼容不裁剪剧情、不提前改原库存，仍生成待确认候选");

        void Reject(JsonNode root, string field)
        {
            bool rejected = false;
            try { InventoryReply.Apply(exchange, root.ToJsonString()); }
            catch (InvalidOperationException e) { rejected = e.Message.Contains(field) && e.Message.Contains("未变"); }
            Check(rejected && state.Items.Count == 1 && state.Quantity(1) == 10,
                "非法 " + field + " 明确指出字段，整轮不提交");
        }
        var nested = fixture.DeepClone(); nested["inventory_ops"]![0]!["attributes"] = new JsonObject { ["嵌套"] = new JsonArray("内容") };
        Reject(nested, "attributes");
        var wrongQuantity = fixture.DeepClone(); wrongQuantity["inventory_ops"]![0]!["quantity"] = "2";
        Reject(wrongQuantity, "quantity");
        var wrongStory = fixture.DeepClone(); wrongStory["story"] = new JsonObject { ["text"] = "不能偷偷转成文字" };
        Reject(wrongStory, "story");
        var missing = fixture.DeepClone(); missing.AsObject().Remove("turn_id");
        Reject(missing, "turn_id");

        string protocol = InventoryReply.Instruction(exchange);
        var examples = protocol.Split('\n').Select(line => line.Trim()).Where(line => line.StartsWith('{')).ToArray();
        Check(examples.Length >= 4 && examples.All(line => { using var doc = JsonDocument.Parse(line); return doc.RootElement.ValueKind == JsonValueKind.Object; }) &&
            protocol.Contains("attributes和effects") && protocol.Contains("所有字段都为字符串"),
            "输出协议包含合法JSON示例及文字字段类型，避免把属性误写成对象");

        string directory = Path.Combine(Path.GetTempPath(), "EraAI-reply-format-" + Guid.NewGuid().ToString("N"));
        try
        {
            var memory = new ConversationStore(directory);
            string content = new(' ', 290), finish = "stop";
            int calls = 0;
            var client = new AiClient(new HttpClient(new Handler(() =>
            {
                calls++;
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new
                { choices = new[] { new { message = new { content }, finish_reason = finish } } }), Encoding.UTF8, "application/json") };
            })), memory);
            var settings = new AiSettings { Mode = "api", BaseUrl = "https://example.invalid/v1", Model = "test", ProtectedKey = SecretProtection.Protect("fake-format-key"), TimeoutSeconds = 10 };
            var request = new AiRequest("资料", "你好", "format_test", "第1天", 1024, 0, "", Inventory: exchange);
            var blank = await client.SendAsync(settings, request, default);
            Check(!blank.Success && blank.Text.Contains("空白字符") && !blank.Text.Contains("调大生成预算") && calls == 1 && memory.Read("format_test").Count == 0,
                "真实空格回复且正常结束时准确提示空白，不误报预算、不自动付费重试");
            finish = "length";
            var truncated = await client.SendAsync(settings, request, default);
            Check(!truncated.Success && truncated.Text.Contains("生成预算") && !truncated.Text.Contains("空白字符") && calls == 2,
                "空白回复实际被服务标记截断时优先报告预算，仍不采用");
            content = "<think>只有思考</think>"; finish = "stop";
            var reasoningOnly = await client.SendAsync(settings, request, default);
            Check(!reasoningOnly.Success && reasoningOnly.Text.Contains("思考片段"), "仅思考片段与真正空白回复分别处理");
            content = JsonSerializer.Serialize(new { story = "纸上写着<think>这是故事中的文字</think>。", turn_id = exchange.TurnId, base_revision = 0,
                inventory_ops = Array.Empty<object>(), state_ops = Array.Empty<object>() });
            var literal = await client.SendAsync(settings, request, default);
            Check(literal.Success && literal.Text == "纸上写着<think>这是故事中的文字</think>。", "JSON正文里的标签作为普通资料保留，不被误删或误判为空");
            content = "";
            var empty = await client.SendAsync(settings, request, default);
            Check(!empty.Success && empty.Text.Contains("空内容") && empty.InventorySnapshot == "", "真正空内容不提交库存、伤势、场景或历史");
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    private sealed class Handler(Func<HttpResponseMessage> respond) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(respond()); }
}
