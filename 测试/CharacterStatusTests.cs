using System.Text.Json;
using MinorShift.Emuera.NativeAI;

internal static class CharacterStatusTests
{
    private static void Check(bool value, string message)
    { if (!value) throw new Exception(message); Console.WriteLine("通过：" + message); }
    private static void Reject(Action action, string message)
    {
        try { action(); } catch (InvalidOperationException) { Check(true, message); return; }
        throw new Exception("未拒绝：" + message);
    }
    internal static void Run()
    {
        var state = new InventoryState();
        state.BindCharacters(new Dictionary<string, string> { ["player"] = "主角", ["ella"] = "艾拉" });
        state.AddInitial(2, "药草", "处理伤口的材料", "消耗品", "", 2, "player", "");
        object Create(string character = "player", string name = "左臂划伤") => new { op = "create_status", character, name,
            category = "伤势", severity = "轻微", description = "左前臂有一道浅伤口，尚未处理。", effects = "拉弓时疼痛", reason = "本轮穿过灌木时划伤" };
        object Update(string character = "player", int status_id = 1) => new { op = "update_status", status_id, character, name = "左臂划伤",
            category = "伤势", severity = "轻微", description = "伤口已清洁并包扎，尚未痊愈。", effects = "用力时仍疼痛", reason = "本轮消耗药草处理伤口" };
        object Remove(string character = "player", int status_id = 1) => new { op = "remove_status", status_id, character, reason = "数日后检查伤口，确认已经痊愈" };
        string Reply(InventoryState current, object[] statuses, object[]? inventory = null, string turn = "status-turn") => JsonSerializer.Serialize(new {
            story = "本轮完整剧情。", turn_id = turn, base_revision = current.Revision, inventory_ops = inventory ?? [], state_ops = statuses });
        InventoryExchange Context(InventoryState current, string turn = "status-turn") => new(current.Snapshot(), turn, GameTime: "第2天 时间3/8");

        string original = state.Snapshot();
        var proposal = InventoryReply.Apply(Context(state), Reply(state, [Create()]));
        var injured = InventoryState.Read(proposal.Snapshot);
        Check(proposal.RequiresApproval && injured.Status(1).Character == "player" && injured.NextStatusId == 2 && state.Statuses.Count == 0,
            "新增伤势分配独立数字编号，仅生成待确认副本，不改当前进度");
        Check(injured.Status(1).CreatedAt == "第2天 时间3/8" && proposal.Changes.Contains("左臂划伤") && proposal.Rationale.Contains("尚未处理"),
            "状态时间由程序给出，预览包含程度、描述、影响与依据");
        var injuryRequest = new AiRequest("人物设定", "你好", "rain_room", "第2天", 1024, 0, "", Inventory: Context(injured));
        Check(PromptBuilder.User(injuryRequest, []).Contains("左前臂") && injured.StatusPage(0)[0].Contains("主角"),
            "有效伤势进入后续提示与人物状态列表，普通输入也不会漏掉已有伤势");
        object consume = new { op = "change_quantity", item_id = 2, owner = "player", delta = -1, reason = "用药" };
        var treatment = InventoryReply.Apply(Context(injured, "treatment"), Reply(injured, [Update()], [consume], "treatment"));
        var treated = InventoryState.Read(treatment.Snapshot);
        Check(treated.Quantity(2) == 1 && treated.Status(1).Description.Contains("尚未痊愈") && treated.Status(1).CreatedAt == injured.Status(1).CreatedAt,
            "用药消耗与包扎更新一起提交，包扎不自动删除伤势");
        var healed = InventoryState.Read(InventoryReply.Apply(Context(treated, "heal"), Reply(treated, [Remove()], turn: "heal")).Snapshot);
        Check(healed.Statuses.Count == 0 && healed.NextStatusId == 2 && !healed.StatusPrompt().Contains("左臂划伤"),
            "痊愈后移出当前有效状态表，保留编号分配进度");
        var again = InventoryState.Read(InventoryReply.Apply(Context(healed, "again"), Reply(healed, [Create()], turn: "again")).Snapshot);
        Check(again.Status(2).Name == "左臂划伤", "移除后重新出现的状态分配新编号");
        Check(InventoryState.Read(treated.Snapshot()).Snapshot() == treated.Snapshot(), "人物状态与物品快照序列化往返一致");
        Check(InventoryState.Read(original).Statuses.Count == 0, "读回早期快照不会残留后续伤势");
        injured.Consume(2, 1, ["player"]);
        Check(injured.Statuses.Count == 1, "程序物品消耗保留已有状态，不擅自恢复人物");

        var invalid = new object[][] {
            [Create(), Create()], [Create("stranger")], [Create("world")], [Remove(status_id: 999)],
            [new { op = "execute", character = "player", reason = "不能执行代码" }],
            [new { op = "create_status", character = "player", name = "伤势", category = "伤势", severity = "轻微", description = "", effects = "", reason = "缺描述" }]
        };
        foreach (var operations in invalid)
        {
            Reject(() => InventoryReply.Apply(Context(state), Reply(state, operations, [consume])), "非法状态使同轮药物消耗一起拒绝");
            Check(state.Snapshot() == original, "失败不会部分更新物品或人物状态");
        }
        Reject(() => InventoryReply.Apply(Context(injured, "wrong"), Reply(injured, [Remove("ella")], turn: "wrong")), "不能移除另一人物的伤势");
        Reject(() => InventoryReply.Apply(Context(injured, "wrong-update"), Reply(injured, [Update("ella")], turn: "wrong-update")), "不能把已有状态更新到另一人物名下");
        Reject(() => InventoryReply.Apply(Context(state), Reply(state, [Create()], [new { op = "change_quantity", item_id = 2, owner = "player", delta = -3, reason = "不足" }])),
            "库存不足时状态也不能单独生效");
        Reject(() => InventoryReply.Apply(Context(state), Reply(state, Enumerable.Repeat(Create(), 33).ToArray())), "状态与物品操作共用单轮操作上限");
        Reject(() => InventoryReply.Apply(new(treated.Snapshot(), "treatment"), Reply(treated, [Remove()], turn: "treatment")), "已结算的状态回合不能重放");
        Reject(() => InventoryReply.Apply(Context(state), Reply(state, [Create()]).Replace("\"state_ops\":[", "\"state_ops\":null,\"ignored\":[")), "拒绝错误列表和额外指令字段");
        string legacyReply = JsonSerializer.Serialize(new { story = "伤势仍然存在。", turn_id = "legacy", base_revision = treated.Revision, inventory_ops = Array.Empty<object>() });
        var retained = InventoryState.Read(InventoryReply.Apply(Context(treated, "legacy"), legacyReply).Snapshot);
        Check(retained.Statuses.Count == 1, "兼容旧回复省略state_ops，省略不会清空已有状态");
        using var document = JsonDocument.Parse(original);
        string legacySnapshot = JsonSerializer.Serialize(document.RootElement.EnumerateObject().Where(p => p.Name is not ("statuses" or "characters" or "next_status_id"))
            .ToDictionary(p => p.Name, p => p.Value.Clone()));
        var migrated = InventoryState.Read(legacySnapshot); migrated.BindCharacters(state.Characters);
        Check(migrated.Statuses.Count == 0 && migrated.NextStatusId == 1 && migrated.Quantity(2) == 2 && migrated.Revision == state.Revision,
            "旧存档补空状态表而不重置物品和回合版本");
        Reject(() => { var corrupt = InventoryState.Read(treated.Snapshot().Replace("\"next_status_id\":2", "\"next_status_id\":1")); }, "损坏状态编号不能静默重建覆盖");
        Reject(() => treated.BindCharacters(new Dictionary<string, string> { ["captain"] = "舰长" }), "另一剧本不能接收原人物的状态记录");

        var definition = ScenarioFixture.Ella;
        var action = definition.Resolve("hunt", 15);
        var hunt = new InventoryExchange(state.Snapshot(), "hunt", action);
        Reject(() => InventoryReply.Apply(hunt, Reply(state, [Create()], turn: "hunt")), "受控狩猎不能擅自追加伤病");
        Check(definition.ContentValue("brief").Contains("中世纪") && definition.Parameter("food_per_day") == 2 && definition.Characters.ContainsKey("ella"),
            "剧本背景、生存参数和人物登记来自统一JSON资料");
    }
}
