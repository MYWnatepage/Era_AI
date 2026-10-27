using System.Text.Json;
using MinorShift.Emuera.NativeAI;
using MinorShift.Emuera.Runtime.Utils;

internal static class ProjectPolishTests
{
    private static void Check(bool value, string message)
    {
        if (!value) throw new Exception(message);
        Console.WriteLine("通过：" + message);
    }
    internal static void Run()
    {
        foreach (string text in new[] { "我不睡觉", "别去打猎", "不许狩猎", "不再打猎", "先打猎再睡觉", "我说：我要去打猎", "狩猎？", "如果我要睡觉", "今天不狩猎，回家休息", "和艾拉聊聊狩猎" })
            Check(ActionRules.CommonAction(text, ScenarioFixture.Ella) == 0, "否定、引用、问句和复合行动不直接执行：" + text);
        Check(ActionRules.CommonAction("我要去打猎！", ScenarioFixture.Ella) == 1 && ActionRules.CommonAction("回家休息。", ScenarioFixture.Ella) == 2, "明确完整的行动指令仍可直接使用");
        var state = new InventoryState();
        state.BindCharacters(ScenarioFixture.Ella.Characters);
        state.AddInitial(2, "食材", "储备食材", "消耗品", "", 10, "player", "");
        state.AddInitial(3, "金币", "金币", "普通物品", "", 2, "player", "");
        var context = new InventoryExchange(state.Snapshot(), "approval-test");
        string Reply(string story, object[] ops, object? suggestions = null) => JsonSerializer.Serialize(new
        { story, turn_id = context.TurnId, base_revision = 0, inventory_ops = ops, suggestions = suggestions ?? Array.Empty<string>() });
        var proposed = InventoryReply.Apply(context, Reply("金币增加。", [new { op = "change_quantity", item_id = 3, owner = "player", delta = 100, reason = "没有已实现的奖励规则" }]));
        Check(proposed.RequiresApproval && proposed.Rationale.Contains("奖励规则") && state.Item(3).Quantities["player"] == 2,
            "没有玩法依据的金币增加成为待确认提案，不修改原库存");
        var noChange = InventoryReply.Apply(context, Reply("艾拉笑了。", []));
        Check(!noChange.RequiresApproval, "没有物品变化的普通对话不增加确认步骤");
        var badTips = InventoryReply.Apply(context, Reply("艾拉笑了。", [], new[] { "一", "二", "三", "四" }));
        Check(badTips.Suggestions == "[]" && badTips.Warning.Contains("建议") && !badTips.RequiresApproval, "不合适的建议可略过，不丢弃有效剧情");
        var hunt = ScenarioFixture.Ella.Resolve("hunt", 9);
        foreach (string badStory in new[] { "你带回99份食材。", "你获得了九十九份食材。", "食材增加了8份。", "本次收获食材五份。" })
        {
            string guarded = ActionNarrative.Check(badStory, hunt, out bool replaced);
            Check(replaced && guarded == hunt.Story, "明显矛盾的狩猎收获不会显示成实际结果");
        }
        foreach (string goodStory in new[] { "你带回3份食材。", "你获得了三份食材。", "现有13份食材。", "艾拉高兴地迎接你回家。" })
            Check(ActionNarrative.Check(goodStory, hunt, out bool replaced) == goodStory && !replaced, "正确收获与当前库存描述保留原剧情");
        var huntContext = new InventoryExchange(state.Snapshot(), "hunt-text-test", hunt);
        var guardedReply = InventoryReply.Apply(huntContext, JsonSerializer.Serialize(new
        { story = "你带回99份食材。", turn_id = huntContext.TurnId, base_revision = 0, inventory_ops = Array.Empty<object>() }));
        Check(InventoryState.Read(guardedReply.Snapshot).Quantity(2) == 13 && guardedReply.Story == hunt.Story && guardedReply.Warning.Contains("不一致") && !guardedReply.RequiresApproval,
            "程序狩猎保持自动结算，矛盾正文与库存同步修正");

        string folder = Path.Combine(Path.GetTempPath(), "EraAI-lease-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        Directory.CreateDirectory(Path.Combine(folder, "independent"));
        try
        {
            using (var first = GameDirectoryLease.Acquire(folder))
            {
                bool blocked = false;
                try { using var second = GameDirectoryLease.Acquire(folder); }
                catch (InvalidOperationException e) { blocked = e.Message.Contains("窗口"); }
                Check(blocked, "同一游戏目录不能重复启动，避免存档与活动记忆相互覆盖");
                using var independent = GameDirectoryLease.Acquire(Path.Combine(folder, "independent"));
                Check(true, "独立游戏目录可分别运行");
            }
            using var restarted = GameDirectoryLease.Acquire(folder);
            Check(true, "窗口结束释放目录占用后可以重新启动");
        }
        finally { Directory.Delete(folder, true); }
    }
}
