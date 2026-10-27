using System.Text.Json;
using MinorShift.Emuera.NativeAI;

internal static class FatigueDicePenaltyTests
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

    private static InventoryState Fresh(ScenarioDefinition scenario, decimal points)
    {
        var state = new InventoryState();
        state.BindOwners(scenario.Owners); state.BindCharacters(scenario.Characters); state.BindWorld(scenario);
        state.SetScene(new() { Location = "狩猎林地", PresentCharacters = ["player"], Summary = "你在林中停下脚步。" });
        state.AddInitial(2, "食材", "可供烹饪的食材。", "消耗品", "", 10, "player", "");
        state.AddInitial(12, "干净布条", "可用于包扎的干净布条。", "消耗品", "用途：包扎", 2, "player", "");
        state.FatiguePoints = points; FatigueSystem.SyncStatus(state);
        return state;
    }

    internal static void Run()
    {
        var scenario = ScenarioFixture.Ella;
        Check(scenario.FatigueRules.Enabled, "当前剧本启用疲劳点与骰点修正");
        foreach (var (points, penalty) in new (decimal, int)[]
            { (0, 0), (24.999m, 0), (25, 2), (49.999m, 2), (50, 4), (74.999m, 4), (75, 6), (100, 6) })
        {
            var state = Fresh(scenario, points); string before = state.Snapshot();
            var result = FatigueDice.Resolve(state, scenario, "hunt", 17);
            int effective = Math.Max(1, 17 - penalty);
            Check(result.RawRoll == 17 && result.Roll == effective && result.FatiguePenalty == penalty &&
                result.Quantity == scenario.Resolve("hunt", effective).Quantity,
                $"疲劳{points}点保留原骰17，按减值{penalty}后的有效骰{effective}决定收获");
            Check(result.Summary.Contains("原始 17") && result.Summary.Contains("有效 " + effective) &&
                result.FatigueCost == scenario.FatigueRules.HuntCost && state.Snapshot() == before,
                "骰点预览展示原始、疲劳与有效值，准备结果不提前增加疲劳或改动状态");
        }
        var severe = Fresh(scenario, 100);
        Check(FatigueDice.Resolve(severe, scenario, "hunt", 1).Roll == 1 &&
            FatigueDice.Resolve(severe, scenario, "hunt", 2).Roll == 1 &&
            FatigueDice.Resolve(severe, scenario, "hunt", 20).Roll == 14,
            "重度疲劳扣骰后最低为1，原始20也按有效14结算，不生成零或负骰点");
        Reject(() => FatigueDice.Resolve(severe, scenario, "hunt", 0), "拒绝低于骰面范围的原始值");
        Reject(() => FatigueDice.Resolve(severe, scenario, "hunt", 21), "拒绝高于骰面范围的原始值");
        Reject(() => FatigueDice.Effective(17, -1), "疲劳修正不能是负减值而变成额外奖励");

        var legacy = ScenarioFixture.Ella; legacy.FatigueRules.Enabled = false;
        var unmodified = FatigueDice.Resolve(severe, legacy, "hunt", 17);
        Check(unmodified.Roll == 17 && unmodified.RawRoll == 0 && unmodified.FatiguePenalty == 0 && unmodified.FatigueCost == 0,
            "未启用疲劳点的剧本保留原骰点与原收益，不追加疲劳费用");

        var tired = Fresh(scenario, 50); string original = tired.Snapshot();
        var prepared = FatigueDice.Resolve(tired, scenario, "hunt", 17);
        var fallback = FatigueDice.Resolve(tired, scenario, "hunt", 17);
        Check(prepared.Roll == fallback.Roll && prepared.RawRoll == fallback.RawRoll && prepared.Quantity == fallback.Quantity &&
            prepared.Summary == fallback.Summary && tired.Snapshot() == original,
            "正常请求与失败回退复用同一原始骰点和行动前疲劳，既不重掷也不提前扣点");
        var exchange = new InventoryExchange(original, "fatigue-dice-adoption", prepared, Scenario: scenario);
        string reply = JsonSerializer.Serialize(new { story = "你收获了 " + prepared.Quantity + " 份食材。", turn_id = exchange.TurnId,
            base_revision = tired.Revision, inventory_ops = Array.Empty<object>(), state_ops = Array.Empty<object>() });
        var adoptedResult = InventoryReply.Apply(exchange, reply); var adopted = InventoryState.Read(adoptedResult.Snapshot);
        Check(adopted.Quantity(2) == 10 + prepared.Quantity && adopted.FatiguePoints == 75 && adopted.Revision == tired.Revision + 1 &&
            tired.Snapshot() == original, "一次采用同时结算有效骰收益与25点狩猎疲劳，原状态保持不变");
        Reject(() => InventoryReply.Apply(new(adoptedResult.Snapshot, exchange.TurnId, fallback, Scenario: scenario), reply),
            "已经采用的骰点不能通过回退再次结算收益和疲劳");

        int woundId = tired.NextStatusId++;
        tired.Statuses.Add(new() { Id = woundId, Character = "player", Name = "左手浅伤", Category = "伤势", Severity = "轻微",
            Description = "左手有一道浅伤，尚未处理。", Effects = "碰触时疼痛。", CreatedAt = "第1天", UpdatedAt = "第1天" });
        string beforeBandage = tired.Snapshot();
        var bandage = ItemActions.Prepare(tired, new(12, "player", "bandage", woundId, 17), "第1天 时间2/8", scenario);
        Check(bandage.Selection.Roll == 17 && bandage.EffectiveRoll == 13 && bandage.FatiguePenalty == 4 &&
            bandage.Quality == "基本包扎" && tired.Snapshot() == beforeBandage,
            "包扎同样保留原始D20并按疲劳修正质量，准备与动画不会消耗材料或疲劳");
        using (var prompt = JsonDocument.Parse(ItemActions.Prompt(bandage)))
            Check(prompt.RootElement.GetProperty("raw_roll").GetInt32() == 17 && prompt.RootElement.GetProperty("roll").GetInt32() == 13 &&
                prompt.RootElement.GetProperty("fatigue_penalty").GetInt32() == 4,
                "包扎给AI的骰点资料明确区分原始、有效和减值，防止模型重复扣减");
        var bandaged = InventoryState.Read(beforeBandage); var changes = new List<string>(); var reasons = new List<string>();
        ItemActions.Apply(bandage, bandaged, changes, reasons);
        Check(bandaged.Quantity(12) == 1 && bandaged.Status(woundId).Description.Contains("尚未痊愈") && bandaged.FatiguePoints == 50 &&
            reasons.Any(reason => reason.Contains("原始 17") && reason.Contains("有效 13")),
            "包扎采用只消耗既定材料并保留伤势，骰子修正本身不额外扣疲劳");
        Reject(() => ItemActions.Apply(bandage, bandaged, [], []), "同一包扎计划不能重复消耗材料");
        var regular = ItemActions.Prepare(tired, new(12, "player", "bandage", woundId), scenario: scenario);
        Check(regular.EffectiveRoll == 0 && regular.FatiguePenalty == 0 && regular.Quality == "常规包扎", "未选掷骰时保留常规包扎，不凭空制造骰点");
        var custom = ScenarioFixture.Ella; custom.FatigueRules.DicePenalties = [0, 1, 3, 5];
        Check(FatigueDice.Resolve(tired, custom, "hunt", 17).Roll == 14 &&
            ItemActions.Prepare(tired, new(12, "player", "bandage", woundId, 17), scenario: custom).EffectiveRoll == 14,
            "自定义疲劳减值在狩猎与包扎中使用同一份剧本规则");
    }
}
