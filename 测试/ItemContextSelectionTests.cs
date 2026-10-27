using System.Text.Json;
using MinorShift.Emuera.NativeAI;

internal static class ItemContextSelectionTests
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
        state.BindCharacters(new Dictionary<string, string> { ["player"] = "主角", ["companion"] = "同伴", ["away"] = "远方人物" });
        state.Owners["companion"] = "同伴"; state.Owners["away"] = "远方人物";
        state.AddInitial(1, "绷带", "完整绷带描述_用于保护伤口。", "消耗品", "材质：亚麻", 2, "player", "");
        state.AddInitial(2, "药膏", "完整药膏描述_有独特药草气味。", "消耗品", "药性：舒缓", 1, "player", "");
        state.AddInitial(3, "随身水壶", "同伴携带的一壶饮水。", "普通物品", "容量：一壶", 1, "companion", "");
        state.AddInitial(4, "异地物品", "远方人物持有。", "普通物品", "", 1, "away", "");
        state.AddInitial(5, "遗落物品", "尚未拾取。", "普通物品", "", 1, "world", "");
        state.AddInitial(6, "已用尽的布片", "数量已经为零。", "消耗品", "", 0, "player", "");
        state.SetScene(new() { Location = "林中营地", PresentCharacters = ["player", "companion"], Summary = "你们停留在火边。" });
        string before = state.Snapshot();
        string quantity = ItemContextSelection.Adjust(state, "", 1, 1);
        Check(quantity == "1:1", "首次左键默认选择1份");
        quantity = ItemContextSelection.Adjust(state, quantity, 1, 1);
        Check(quantity == "1:2" && ItemContextSelection.Adjust(state, quantity, 1, 10) == "1:2", "左键增加数量并在库存上限停止");
        Check(ItemContextSelection.Adjust(state, quantity, 1, -1) == "1:1" &&
            ItemContextSelection.Adjust(state, "1:1", 1, -1) == "", "右键减到零取消选择");
        Check(ItemContextSelection.Adjust(state, "", 1, -1) == "", "未选物品右键不误选成1");
        Check(ItemContextSelection.Adjust(state, quantity, 1, -10) == "", "向下减10截到零取消选择");
        Reject(() => ItemContextSelection.ValidateAmounts(state, "1:3"), "发送前拒绝超过实际库存的关联数量");
        foreach (string text in new[] { "1:0", "1:-2", "1:abc", "1:1:2" })
            Reject(() => ItemContextSelection.Amounts(text), "拒绝无效数量：" + text);
        Check(state.Snapshot() == before, "数量操作不提前消耗库存");
        var quantityExchange = new InventoryExchange(before, "quantity-check", SelectedItems: [1], SelectedAmounts: new Dictionary<int,int>{{1,1}});
        string ConsumeReply(int count) => JsonSerializer.Serialize(new { story = "你按选择使用材料。", turn_id = "quantity-check", base_revision = state.Revision,
            inventory_ops = new[] { new { op = "change_quantity", item_id = 1, owner = "player", delta = -count, reason = "本次使用" } }, state_ops = Array.Empty<object>() });
        Reject(() => InventoryReply.Apply(quantityExchange, ConsumeReply(2)), "AI不能扣除超过玩家选择的数量");
        Check(InventoryReply.Apply(quantityExchange, ConsumeReply(1)).RequiresApproval && state.Snapshot() == before,
            "所选数量以内仍须确认，未提前扣除库存");
        Check(ItemContextSelection.Parse(" 1, 2,1 ").SequenceEqual([1, 2]), "关联列表使用数字编号、保留选择顺序并去重");
        Check(ItemContextSelection.Parse("").Length == 0, "没有关联物品时正常继续自定义输入");
        foreach (string csv in new[] { "0", "-1", "1.0", "1,x", "1,,2", ",1", "1,", "2147483647" })
            Reject(() => ItemContextSelection.Parse(csv), "拒绝格式无效的物品关联列表：" + csv);
        string selected = ItemContextSelection.Toggle(state, "", 1);
        selected = ItemContextSelection.Toggle(state, selected, 2);
        selected = ItemContextSelection.Toggle(state, selected, 3);
        Check(selected == "1,2,3", "输入行动前可以同时关联自己的多个物品与在场人物持有的物品");
        Check(ItemContextSelection.Toggle(state, selected, 2) == "1,3", "再次点击已选物品只取消该项，不清空其他选择");
        foreach (int id in new[] { 4, 5, 6, 99, 0, -1 })
            Reject(() => ItemContextSelection.Select(state, [id]), "拒绝远处、无主、耗尽或不存在的关联物品：" + id);
        Check(state.Snapshot() == before, "选择、取消与拒绝不消耗物品、不改变归属、不推进状态版本");

        var outcome = new ScenarioActionOutcome(12, 2, 2, "player", "受控行动结果", "既定过程", "按既定结果续写", [], 20);
        var prepared = new InventoryExchange(before, "selection-turn", outcome, "selection-test", SelectedItem: new(1, "player", "context"));
        int[] callerIds = [1, 2, 3];
        var exchange = ItemContextSelection.Attach(prepared, callerIds);
        callerIds[0] = 99;
        Check(exchange.Action == outcome && exchange.SelectedItem == prepared.SelectedItem && exchange.TurnId == prepared.TurnId &&
            exchange.SelectedItems!.SequenceEqual([1, 2, 3]), "多选资料附加到已准备行动，保留D20与受保护物品计划，并复制选择以免外部修改");
        Check(ItemContextSelection.Matches(exchange, before, "selection-test") && !ItemContextSelection.Matches(exchange, before, "other-scenario"),
            "只有相同剧本的同一快照可复用已准备行动");
        var changed = InventoryState.Read(before); changed.Revision++;
        Check(!ItemContextSelection.Matches(exchange, changed.Snapshot(), "selection-test"), "物品版本改变后不能复用旧行动准备");
        changed = InventoryState.Read(before); changed.Item(1).Quantities["player"]--;
        Check(!ItemContextSelection.Matches(exchange, changed.Snapshot(), "selection-test"), "即使版本未变化，实际快照不同也不能复用旧准备");

        var request = new AiRequest("简短场景资料", "我拿这些东西试着做一次处理。", "selection-test", "第2天 时间1/8", 1024, 0, "", Inventory: exchange);
        using var prompt = JsonDocument.Parse(PromptBuilder.User(request, []));
        var context = prompt.RootElement.GetProperty("program_context");
        var cards = context.GetProperty("selected_items");
        Check(cards.GetArrayLength() == 3 && cards[0].GetProperty("id").GetInt32() == 1 &&
            cards[0].GetProperty("description").GetString() == state.Item(1).Description &&
            cards[1].GetProperty("attributes").GetString() == state.Item(2).Attributes &&
            cards[2].GetProperty("amounts").GetProperty("companion").GetInt32() == 1,
            "无需输入物品名称，明确选择的每张完整卡都会进入本轮资料，包含定义与准确归属数量");
        Check(context.GetProperty("inventory").GetProperty("items").EnumerateArray().All(item =>
            !exchange.SelectedItems!.Contains(item.GetProperty("id").GetInt32())), "完整选中物品卡不在普通相关物品列表中重复发送");
        Check(context.TryGetProperty("selected_item", out _) && context.TryGetProperty("program_action", out _) &&
            prompt.RootElement.GetProperty("player_input").GetString() == request.Player, "多选资料与已有物品用法、程序判定兼容，保持玩家原话不混入技术指令");
        Check(state.Snapshot() == before, "组装完整多选上下文仍不提前修改当前物品和状态");
        using var ordinary = JsonDocument.Parse(PromptBuilder.User(request with { Inventory = new(before, "next-turn") }, []));
        Check(!ordinary.RootElement.GetProperty("program_context").TryGetProperty("selected_items", out _), "下一次普通请求不继承上一轮的临时物品选择");

        var many = InventoryState.Read(before);
        for (int id = 7; id <= 23; id++) many.AddInitial(id, "材料" + id, "一份可用材料。", "普通物品", "", 1, "player", "");
        int[] sixteen = Enumerable.Range(7, 16).ToArray();
        Check(ItemContextSelection.Select(many, sixteen).Length == 16, "一次可明确关联最多16种物品");
        Reject(() => ItemContextSelection.Toggle(many, string.Join(",", sixteen), 23), "第17种选择在发送前拒绝，防止无意附带整个仓库");
        Reject(() => ItemContextSelection.Parse(string.Join(",", Enumerable.Range(7, 17))), "从输入解析的选择同样受16种上限保护");
    }
}
