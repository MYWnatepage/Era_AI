using System.Text.Json;
using MinorShift.Emuera.NativeAI;

internal static class ActionTests
{
    private static void Check(bool value, string message)
    {
        if (!value) throw new Exception(message);
        Console.WriteLine("通过：" + message);
    }
    internal static void Run()
    {
        int[] expected = [0, 0, 0, 1, 1, 1, 1, 1, 3, 3, 3, 3, 3, 3, 5, 5, 5, 5, 8, 8];
        for (int roll = 1; roll <= 20; roll++)
        {
            var outcome = ScenarioFixture.Ella.Resolve("hunt", roll);
            Check(outcome.Roll == roll && outcome.Quantity == expected[roll - 1], $"D20={roll}的收获由程序固定计算");
            var state = new InventoryState();
            state.BindCharacters(ScenarioFixture.Ella.Characters);
            state.AddInitial(2, "食材", "饭食储备", "消耗品", "烹饪", 10, "player", "食材");
            var context = new InventoryExchange(state.Snapshot(), "hunt-test-" + roll, outcome);
            string response = JsonSerializer.Serialize(new { story = "你完成狩猎并回到家中。", turn_id = context.TurnId,
                base_revision = state.Revision, inventory_ops = Array.Empty<object>(), suggestions = new[] { "和艾拉聊聊", "回家休息" } });
            var result = InventoryReply.Apply(context, response);
            Check(InventoryState.Read(result.Snapshot).Quantity(2) == 10 + expected[roll - 1], "AI只返回剧情时，程序仍按骰点准确入库");
            Check(result.Changes.Contains("D20：" + roll) && ActionRules.SuggestionList(result.Suggestions).Length == 2, "骰点摘要和下一步建议可供界面读取");
            string tampered = response.Replace("\"inventory_ops\":[]", "\"inventory_ops\":[{\"op\":\"change_quantity\",\"item_id\":2,\"owner\":\"player\",\"delta\":99,\"reason\":\"额外狩猎收益\"}]");
            bool rejected = false;
            try { InventoryReply.Apply(context, tampered); } catch (InvalidOperationException) { rejected = true; }
            Check(rejected && state.Quantity(2) == 10, "狩猎回复不能追加、篡改或重复发放物品收益");
        }
        foreach (int invalid in new[] { 0, 21, -1 })
        {
            bool rejected = false;
            try { ScenarioFixture.Ella.Resolve("hunt", invalid); } catch (ArgumentException) { rejected = true; }
            Check(rejected, "拒绝超出D20范围的骰点");
        }
        foreach (string direct in new[] { "外出狩猎", "我要去打猎", "开始捕猎" })
            Check(ActionRules.CommonAction(direct, ScenarioFixture.Ella) == 1, "直接输入狩猎行动也走程序骰子规则");
        foreach (string question in new[] { "我不去狩猎", "如何狩猎？", "明天计划去打猎", "给我讲讲狩猎规则", "和艾拉聊聊今天的狩猎", "回顾昨天的狩猎", "狩猎归来后我们做什么？" })
            Check(ActionRules.CommonAction(question, ScenarioFixture.Ella) == 0, "询问、否定、假设和未来计划不自动执行狩猎");
        Check(ActionRules.CommonAction("回家休息", ScenarioFixture.Ella) == 2 && ActionRules.CommonAction("休息到明天", ScenarioFixture.Ella) == 2, "明确休息行动使用日末结算");
        Check(ActionRules.SuggestionList("", ScenarioFixture.Ella.DefaultSuggestions).Length == 2 && ActionRules.SuggestionList("broken", ScenarioFixture.Ella.DefaultSuggestions).Length == 2, "缺少建议时使用可用的日常行动");
        using var valid = JsonDocument.Parse("[\"聊聊今天的见闻\",\"聊聊今天的见闻\",\"休息到明天\"]");
        Check(ActionRules.SuggestionList(ActionRules.Suggestions(valid.RootElement)).Length == 2, "重复建议合并而不改变编号规则");
        using var invalidSuggestions = JsonDocument.Parse("[\"a\",\"b\",\"c\",\"d\"]");
        bool tooMany = false;
        try { ActionRules.Suggestions(invalidSuggestions.RootElement); } catch (ArgumentException) { tooMany = true; }
        Check(tooMany, "建议行动最多三条");
        Check(ScenarioFixture.Ella.Parameter("ticks_per_day") == 8 && ScenarioFixture.Ella.Parameter("hunt_ticks") == 4, "一天8格，狩猎半天的规则来源统一");
    }
}
