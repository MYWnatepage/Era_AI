using System.Net;
using System.Text;
using System.Text.Json;
using MinorShift.Emuera.NativeAI;
using MinorShift.Emuera.Runtime.Utils;

internal static class InventoryTests
{
    private static void Check(bool condition, string description)
    {
        if (!condition) throw new Exception(description);
        Console.WriteLine("通过：" + description);
    }
    private static void Reject(Action action, string description)
    {
        try { action(); } catch (InvalidOperationException) { Check(true, description); return; }
        throw new Exception("未拒绝：" + description);
    }
    internal static async Task Run()
    {
        foreach (string value in new[] { "普通旧档文本", "第一行\n第二行", "回车\r换行\r\n", "__FINISHED", "__EMU_SEPARATOR__", "__ERA_AI_TEXT_V1__:AAAA", "" })
        {
            string encoded = SaveStringCodec.Encode(value);
            Check(!encoded.Contains('\n') && !encoded.Contains('\r') && SaveStringCodec.Decode(encoded) == value, "多行剧情及特殊标记在文本存档中往返不串位");
        }
        Check(SaveStringCodec.Decode("旧版原始字符串") == "旧版原始字符串", "继续读取未编码的旧存档字符串");
        var state = new InventoryState();
        state.AddInitial(1, "钥匙", "住宅钥匙", "普通物品", "开门", 2, "player", "钥匙");
        state.AddInitial(2, "食材", "饭食储备", "消耗品", "烹饪", 10, "player", "食材");
        state.AddInitial(11, "毽子", "艾拉的毽子", "普通物品", "玩具", 1, "ella", "毽子");
        var context = new InventoryExchange(state.Snapshot(), "turn-test-1");
        string Reply(object[] ops, string turn = "turn-test-1", int revision = 0) => JsonSerializer.Serialize(new
        { story = "你带着食材回到家，把一把钥匙交给艾拉，又收好新找到的石头。", turn_id = turn, base_revision = revision, inventory_ops = ops });
        var operations = new object[]
        {
            new { op = "change_quantity", item_id = 2, owner = "player", delta = 3, reason = "狩猎所得" },
            new { op = "transfer_item", item_id = 1, from_owner = "player", to_owner = "ella", quantity = 1, reason = "交给艾拉" },
            new { op = "create_item", name = "月光石", description = "泛着淡蓝光的石头。", type = "特殊物品", attributes = "用途未知", quantity = 1, owner = "player", reason = "林中拾得" },
        };
        var result = InventoryReply.Apply(context, Reply(operations));
        var next = InventoryState.Read(result.Snapshot);
        Check(next.Quantity(2) == 13 && next.Item(1).Quantities["player"] == 1 && next.Item(1).Quantities["ella"] == 1, "同轮增加食材并部分转移归属");
        Check(next.Item(12).Description == "泛着淡蓝光的石头。" && next.NextId == 13 && next.Revision == 1, "新物品自动分配数字编号，描述与数量一同保存");
        Check(InventoryState.Read(next.Snapshot()).Snapshot() == next.Snapshot(), "完整物品快照往返一致");
        Check(state.Quantity(2) == 10 && state.Items.Count == 3, "结算使用副本，不修改初始物品或原快照");
        Reject(() => InventoryReply.Apply(new(result.Snapshot, context.TurnId), Reply(operations, revision: 1)), "同一回合不能重复结算");
        Reject(() => InventoryReply.Apply(context, Reply([], turn: "other-turn")), "拒绝不属于本次请求的回复");
        Reject(() => InventoryReply.Apply(context, Reply([], revision: 9)), "拒绝过期物品版本");
        var invalidBatches = new object[][]
        {
            [operations[0], new { op = "change_quantity", item_id = 1, owner = "player", delta = -3, reason = "库存不足" }],
            [new { op = "change_quantity", item_id = 999, owner = "player", delta = 1, reason = "不存在" }],
            [new { op = "change_quantity", item_id = 2, owner = "player", delta = 1.5, reason = "小数" }],
            [new { op = "change_quantity", item_id = 2, owner = "player", delta = int.MaxValue, reason = "溢出" }],
            [new { op = "change_quantity", item_id = 2, owner = "stranger", delta = 1, reason = "无效归属" }],
            [new { op = "create_item", name = "食材", description = "重复", type = "消耗品", attributes = "", quantity = 1, owner = "player", reason = "应增量" }],
            [new { op = "create_item", name = "石头", description = "", type = "普通物品", attributes = "", quantity = 1, owner = "player", reason = "缺描述" }],
            [new { op = "create_item", name = "石头", description = "石头", type = "随意类型", attributes = "", quantity = 1, owner = "player", reason = "错类型" }],
            [new { op = "transfer_item", item_id = 1, from_owner = "player", to_owner = "ella", quantity = 3, reason = "不足" }],
            [new { op = "transfer_item", item_id = 1, from_owner = "player", to_owner = "player", quantity = 1, reason = "相同归属" }],
            [new { op = "run_script", script = "QUIT", reason = "不能执行任意代码" }],
            [new { op = "change_quantity", item_id = 2, owner = "player", delta = 1, reason = "额外字段", path = "anything" }],
        };
        foreach (var batch in invalidBatches) Reject(() => InventoryReply.Apply(context, Reply(batch)), "非法操作整轮拒绝，原库存保持不变");
        Reject(() => InventoryReply.Apply(context, "{\"story\":\"a\",\"story\":\"b\"}"), "重复JSON字段无效");
        Reject(() => InventoryReply.Apply(context, "这次获得食材3份。"), "缺少操作协议不凭叙述修改库存");
        Reject(() => InventoryReply.Apply(context, Reply(operations)[..^5]), "截断协议不执行");
        Reject(() => InventoryState.Read("{}"), "不把缺少字段的损坏快照当新游戏");
        Reject(() => InventoryState.Read(context.Snapshot.Replace("\"version\":1", "\"version\":99")), "未知物品格式版本不覆盖旧档");
        next.Consume(2, 2, ["player", "ella"]);
        Check(next.Quantity(2) == 11, "每日消耗修改同一份当前物品表");
        next.SetQuantity(2, "player", 1); next.Consume(2, 2, ["player", "ella"]);
        Check(next.Quantity(2) == 0 && next.Item(2).Quantities["player"] == 0, "每日消耗最多扣至零且保留物品描述");
        Check(state.Page(0)[0].Contains("钥匙") && state.Page(0)[11] == "3", "物品列表提供编号、数量、归属与分页");
        Check(state.Prompt("查看钥匙").Contains("住宅钥匙") && !state.Prompt("你好").Contains("住宅钥匙"), "只在提及物品时附带完整描述");

        var directory = Path.Combine(Path.GetTempPath(), "EraAI-inventory-" + Guid.NewGuid().ToString("N"));
        try
        {
            var memory = new ConversationStore(directory);
            string reply = Reply(operations);
            string? finish = "stop";
            var client = new AiClient(new HttpClient(new FakeHandler(async (request, cancellation) =>
            {
                using var sent = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellation));
                string system = sent.RootElement.GetProperty("messages")[0].GetProperty("content").GetString()!;
                var messages = sent.RootElement.GetProperty("messages");
                using var turn = JsonDocument.Parse(messages[messages.GetArrayLength() - 1].GetProperty("content").GetString()!);
                Check(system.Contains("inventory_ops") && !system.Contains("turn-test-1") &&
                    turn.RootElement.GetProperty("program_context").GetProperty("turn_id").GetString() == "turn-test-1",
                    "稳定协议与本轮资料分别传入，模型仍收到正确回合标识");
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new
                { choices = new[] { new { message = new { content = reply }, finish_reason = finish } } }), Encoding.UTF8, "application/json") };
            })), memory);
            var settings = new AiSettings { Mode = "api", BaseUrl = "https://example.invalid/v1", Model = "test", ProtectedKey = SecretProtection.Protect("fake-key"), TimeoutSeconds = 10 };
            var request = new AiRequest("狩猎归来", "狩猎归来", "rain_room", "第1天", 1024, 0, "", Inventory: context);
            var received = await client.SendAsync(settings, request, default);
            Check(received.Success && received.InventorySnapshot == result.Snapshot && received.Text == result.Story, "AI回复拆分为剧情、已校验物品快照和变化摘要");
            Check(received.RequiresApproval && received.Rationale.Contains("狩猎所得") && memory.Read("rain_room").Count == 0, "普通物品变更附带依据，确认前不写入对话记忆");
            int count = memory.Read("rain_room").Count;
            reply = Reply(invalidBatches[0]);
            var bad = await client.SendAsync(settings, request, default);
            Check(!bad.Success && bad.InventorySnapshot == "" && memory.Read("rain_room").Count == count, "结算失败不返回新库存、不污染历史");
            reply = Reply(operations); finish = "length";
            var clipped = await client.SendAsync(settings, request, default);
            Check(!clipped.Success && memory.Read("rain_room").Count == count, "达到输出上限时不执行可能不完整的操作");
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }
}
