using System.Text.Json;
using MinorShift.Emuera.NativeAI;

internal static class ItemActionTests
{
    private static void Check(bool value, string message)
    { if (!value) throw new Exception(message); Console.WriteLine("通过：" + message); }
    private static void Reject(Action action, string message)
    {
        try { action(); }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException)
        { Check(true, message); return; }
        throw new Exception("未拒绝：" + message);
    }
    internal static void Run()
    {
        var state = new InventoryState();
        state.BindCharacters(new Dictionary<string, string> { ["player"] = "主角", ["companion"] = "同行者", ["away"] = "远行者" });
        state.Owners["companion"] = "同行者"; state.Owners["away"] = "远行者";
        state.AddInitial(1, "布条", "干净的亚麻布条", "普通物品", "材质：亚麻", 2, "player", "");
        state.AddInitial(2, "铜钥匙", "打开门锁", "普通物品", "", 1, "player", "");
        state.AddInitial(3, "药用贴片", "封装的处理材料", "消耗品", "用途：包扎", 2, "player", "");
        state.AddInitial(4, "绷带", "尚在别处", "消耗品", "", 1, "away", "");
        state.AddInitial(5, "纱布", "同行者携带", "消耗品", "", 1, "companion", "");
        state.AddInitial(6, "空布条", "已用尽", "普通物品", "", 0, "player", "");
        state.Scene = new() { Location = "林边", PresentCharacters = ["player", "companion"], Summary = "准备处理伤口" };
        state.Statuses = [
            new() { Id = 1, Character = "companion", Name = "手指划伤", Category = "伤势", Severity = "轻微", Description = "指尖有浅伤口，尚未处理。", Effects = "按压疼痛", CreatedAt = "第1天", UpdatedAt = "第1天" },
            new() { Id = 2, Character = "away", Name = "手臂划伤", Category = "伤势", Severity = "轻微", Description = "远处人物的浅伤口。", Effects = "", CreatedAt = "第1天", UpdatedAt = "第1天" },
            new() { Id = 3, Character = "player", Name = "疲劳", Category = "体力", Severity = "轻微", Description = "需要休息。", Effects = "", CreatedAt = "第1天", UpdatedAt = "第1天" },
        ];
        state.NextStatusId = 4;
        string original = state.Snapshot();
        var link = ItemActions.Prepare(state, new(1, "player", "context"));
        var linked = InventoryState.Read(original);
        var changes = new List<string>(); var rationale = new List<string>();
        ItemActions.Apply(link, linked, changes, rationale);
        Check(link.Cost == 0 && linked.Snapshot() == original && changes.Count == 0,
            "关联物品仅提供资料，不因选中物品自动消耗或治疗");
        using (var prompt = JsonDocument.Parse(ItemActions.Prompt(link)))
            Check(prompt.RootElement.GetProperty("mode").GetString() == "context" &&
                prompt.RootElement.GetProperty("instruction").GetString()!.Contains("尚未授权"), "关联提示区分意图和已确定效果");

        var plan = ItemActions.Prepare(state, new(1, "player", "bandage", 1), "第1天 时间2/8");
        Check(state.Snapshot() == original && plan.IsTreatment && plan.Cost == 1 && plan.Selection.Roll == 0,
            "明确选择包扎后只创建不可变候选计划，尚不修改当前物品与伤势");
        var candidate = InventoryState.Read(original);
        ItemActions.Apply(plan, candidate, changes, rationale);
        Check(candidate.Quantity(1) == 1 && candidate.Status(1).Description.Contains("尚未痊愈") &&
            candidate.Status(1).Effects.Contains("按压疼痛") && candidate.Statuses.Count == state.Statuses.Count,
            "包扎消耗一份材料、保留原伤势影响，不能移除伤势或自动痊愈");
        Check(candidate.Status(1).UpdatedAt == "第1天 时间2/8" && candidate.Status(1).CreatedAt == "第1天" &&
            candidate.Revision == state.Revision && state.Snapshot() == original,
            "处理时间由程序提供，候选不提前增加版本或改写原状态");
        Check(changes.Any(text => text.Contains("-1")) && rationale.Any(text => text.Contains("玩家明确选择")),
            "本地消费与包扎依据进入同一次确认预览");
        Reject(() => ItemActions.Prepare(candidate, new(1, "player", "bandage", 1)), "拒绝对已包扎伤势反复扣除材料");
        Reject(() => ItemActions.Apply(plan, candidate, [], []), "同一计划不能对已经修改的候选重复应用");

        var low = ItemActions.Prepare(state, new(1, "player", "bandage", 1, 1));
        var high = ItemActions.Prepare(state, new(1, "player", "bandage", 1, 20));
        Check(low.Quality != high.Quality && low.Cost == high.Cost && low.Description.Contains("未痊愈") && high.Description.Contains("未痊愈"),
            "D20只决定包扎质量，所有判定保留伤势且消耗固定");
        Reject(() => ItemActions.Prepare(state, new(1, "player", "bandage", 1, 21)), "拒绝伪造超范围D20骰点");
        Reject(() => ItemActions.Prepare(state, new(1, "player", "bandage", 1, -1)), "拒绝负骰点");
        Reject(() => ItemActions.Prepare(state, new(1, "player", "unknown", 1)), "拒绝未知用法");
        Reject(() => ItemActions.Prepare(state, new(1, "player", "context", 1)), "关联模式不能夹带目标伤势");
        Reject(() => ItemActions.Prepare(state, new(1, "player", "context", Roll: 8)), "关联模式不能夹带骰点");
        Reject(() => ItemActions.Prepare(state, new(2, "player", "bandage", 1)), "不能仅靠选中任意物品将钥匙当绷带消耗");
        Reject(() => ItemActions.Prepare(state, new(1, "player", "bandage")), "包扎必须选择明确的现有伤势");
        Reject(() => ItemActions.Prepare(state, new(1, "player", "bandage", 2)), "不在场人物不能凭选择直接受到包扎");
        Reject(() => ItemActions.Prepare(state, new(1, "player", "bandage", 3)), "包扎不能处理疲劳等非伤势状态");
        Reject(() => ItemActions.Prepare(state, new(6, "player", "bandage", 1)), "数量为零的材料不能生成使用计划");
        Reject(() => ItemActions.Prepare(state, new(4, "away", "context")), "不能关联远处人物持有的物品");
        Reject(() => ItemActions.Prepare(state, new(5, "companion", "bandage", 1)), "包扎不能直接扣除尚未交给主角的他人物品");
        Check(ItemActions.Prepare(state, new(5, "companion", "context")).Cost == 0,
            "可以明确关联在场人物所持物品，仍不默认转交或消耗");
        Check(ItemActions.Prepare(state, new(3, "player", "bandage", 1)).IsTreatment,
            "剧本或动态物品可用明确用途属性登记包扎能力，不写死特定剧本或人物");

        var longRecord = InventoryState.Read(original);
        longRecord.Status(1).Description = new string('伤', 850) + "末尾关键事实：伤口不可浸水。";
        longRecord.Status(1).Effects = new string('痛', 400) + "末尾影响：手指仍不能弯曲。";
        string longOriginal = longRecord.Snapshot();
        var longPlan = ItemActions.Prepare(longRecord, new(1, "player", "bandage", 1));
        Check(longPlan.Description.StartsWith(longRecord.Status(1).Description, StringComparison.Ordinal) &&
            longPlan.Effects.StartsWith(longRecord.Status(1).Effects, StringComparison.Ordinal) && longRecord.Snapshot() == longOriginal,
            "包扎完整保留较长伤势描述与影响的末尾事实，不静默截断已保存资料");
        longRecord.Status(1).Description = new string('伤', 1000);
        longOriginal = longRecord.Snapshot();
        Reject(() => ItemActions.Prepare(longRecord, new(1, "player", "bandage", 1)),
            "已有伤势描述无空间附加包扎时在请求前拒绝，不截断原记录");
        Check(longRecord.Snapshot() == longOriginal, "过长描述拒绝后原伤势与材料数量完全不变");
        longRecord = InventoryState.Read(original);
        longRecord.Status(1).Effects = new string('痛', 500);
        longOriginal = longRecord.Snapshot();
        Reject(() => ItemActions.Prepare(longRecord, new(1, "player", "bandage", 1)),
            "已有伤势影响无空间附加包扎时在请求前拒绝，不截断原记录");
        Check(longRecord.Snapshot() == longOriginal, "过长影响拒绝后原伤势与材料数量完全不变");

        void Guard(object[] items, object[] statuses, bool reject = true)
        {
            using var doc = JsonDocument.Parse(JsonSerializer.Serialize(new { inventory_ops = items, state_ops = statuses }));
            if (reject) Reject(() => ItemActions.ValidateModelOperations(plan, doc.RootElement), "拒绝模型重复结算受保护材料或包扎伤势");
            else ItemActions.ValidateModelOperations(plan, doc.RootElement);
        }
        Guard([new { op = "change_quantity", item_id = 1, owner = "player", delta = -1 }], []);
        Guard([new { op = "transfer_item", item_id = 1, from_owner = "player", to_owner = "companion", quantity = 1 }], []);
        Guard([], [new { op = "update_status", status_id = 1, character = "companion" }]);
        Guard([], [new { op = "remove_status", status_id = 1, character = "companion" }]);
        Guard([], [new { op = "create_status", name = "手指划伤", character = "companion" }]);
        Guard([new { op = "change_quantity", item_id = 2, owner = "player", delta = -1 }],
            [new { op = "update_status", status_id = 3, character = "player" }], false);
        Check(true, "同轮仍允许独立的其他物品或状态变化，全部等待统一确认");
        using (var doc = JsonDocument.Parse("{\"inventory_ops\":[{\"item_id\":1}],\"state_ops\":[]}"))
            ItemActions.ValidateModelOperations(link, doc.RootElement);
        Check(true, "仅关联物品时不阻止AI按正常协议提出实际物品操作");

        var changed = InventoryState.Read(original); changed.Revision++;
        Reject(() => ItemActions.Apply(plan, changed, [], []), "本地行动计划不接受其他版本的状态");
        changed = InventoryState.Read(original); changed.LastTurnId = "other-turn";
        Reject(() => ItemActions.Apply(plan, changed, [], []), "本地行动计划不接受已发生其他回合的状态");
        changed = InventoryState.Read(original); changed.Item(1).Quantities["player"]--;
        string beforeFailure = changed.Snapshot();
        Reject(() => ItemActions.Apply(plan, changed, [], []), "候选材料被重复修改时拒绝整个计划");
        Check(changed.Snapshot() == beforeFailure, "材料冲突不会部分写入人物状态");
        changed = InventoryState.Read(original); changed.Status(1).Description = "另一条伤势现状";
        beforeFailure = changed.Snapshot();
        Reject(() => ItemActions.Apply(plan, changed, [], []), "候选伤势被重复修改时拒绝整个计划");
        Check(changed.Snapshot() == beforeFailure, "伤势冲突不会部分扣除物品");

        string Reply(object[]? items = null, object[]? statuses = null, string turn = "item-use") => JsonSerializer.Serialize(new
        {
            story = "你仔细完成包扎，伤口仍需休养。", turn_id = turn, base_revision = state.Revision,
            inventory_ops = items ?? [], state_ops = statuses ?? [], suggestions = Array.Empty<string>(),
        });
        var exchange = new InventoryExchange(original, "item-use", GameTime: "第2天 时间0/8", SelectedItem: plan.Selection);
        var proposed = InventoryReply.Apply(exchange, Reply());
        var adopted = InventoryState.Read(proposed.Snapshot);
        Check(proposed.RequiresApproval && adopted.Quantity(1) == 1 && adopted.Status(1).Description.Contains("程序包扎") &&
            adopted.Revision == state.Revision + 1 && adopted.LastTurnId == "item-use" && state.Snapshot() == original,
            "完整回复解析将本地物品使用作为一次待确认候选，统一推进一个版本");
        Reject(() => InventoryReply.Apply(exchange, Reply([new { op = "change_quantity", item_id = 1, owner = "player", delta = -1,
            reason = "模型重复扣料" }])), "完整协议拒绝AI重复扣除本地包扎材料");
        Reject(() => InventoryReply.Apply(exchange, Reply(statuses: [new { op = "remove_status", status_id = 1, character = "companion",
            reason = "模型擅自痊愈" }])), "完整协议拒绝AI覆盖本地确定的伤势结果");
        Reject(() => InventoryReply.Apply(exchange, Reply([new { op = "change_quantity", item_id = 2, owner = "player", delta = -2,
            reason = "另一项数量不足" }])), "同批其他物品操作无效时，本地包扎不会单独扣料或生效");
        Check(state.Snapshot() == original, "所有失败仍保留原始物品和人物状态快照");
        var linkedExchange = exchange with { SelectedItem = link.Selection };
        var ordinaryChange = InventoryState.Read(InventoryReply.Apply(linkedExchange,
            Reply([new { op = "change_quantity", item_id = 1, owner = "player", delta = -1, reason = "玩家实际执行普通用法" }])).Snapshot);
        Check(ordinaryChange.Quantity(1) == 1 && !ordinaryChange.Status(1).Description.Contains("程序包扎"),
            "关联模式保留普通变更协议，不因被选中就自动套用包扎规则");
    }
}
