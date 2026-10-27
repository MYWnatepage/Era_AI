using System.Text.Json;
using MinorShift.Emuera.NativeAI;

internal static class FatigueRecoveryTests
{
    private static void Check(bool value, string text)
    { if (!value) throw new Exception(text); Console.WriteLine("通过：" + text); }

    private static int Add(InventoryState state, string name, string category, string severity, string character = "player")
    {
        int id = state.NextStatusId++;
        state.Statuses.Add(new() { Id = id, Character = character, Name = name, Category = category, Severity = severity,
            Description = name + "仍然存在。", Effects = "影响持续活动。", CreatedAt = "第1天", UpdatedAt = "第1天" });
        return id;
    }

    internal static void Run()
    {
        var scenario = ScenarioFixture.Ella;
        scenario.FatigueRules.Enabled = false; // Explicitly exercise legacy category/degree packages.
        Check(scenario.RecoveryRules.Values.All(rule => rule.StatusNames.Length == 0),
            "当前剧本按稳定状态类别恢复，不限制AI所写的具体疲劳名称");
        scenario.Validate();
        var state = new InventoryState(); state.BindOwners(scenario.Owners); state.BindCharacters(scenario.Characters);
        state.BindWorld(scenario); state.SetScene(scenario.InitialScene);

        // Use the same structured response path as the real-play bug, rather than only
        // constructing a status that the live protocol could not have produced.
        var exchange = new InventoryExchange(state.Snapshot(), "fatigue-created", GameTime: "第1天 时间2/8");
        string response = JsonSerializer.Serialize(new { story = "搬运后你感到疲倦，指尖的浅伤尚未痊愈。",
            turn_id = exchange.TurnId, base_revision = state.Revision, inventory_ops = Array.Empty<object>(), state_ops = new object[] {
                new { op = "create_status", character = "player", name = "搬运疲劳", category = " 疲劳 ", severity = " 轻微 ",
                    description = "搬运后四肢酸乏。", effects = "持续活动较吃力。", reason = "本轮搬运物资。" },
                new { op = "create_status", character = "player", name = "左手食指划伤", category = "伤势", severity = "轻微",
                    description = "已经包扎但尚未愈合。", effects = "按压疼痛。", reason = "本轮检查伤口。" }
            } });
        var created = InventoryState.Read(InventoryReply.Apply(exchange, response).Snapshot);
        Check(created.Status(1).Name == "疲劳" && created.Status(1).Category == "疲劳" && created.Status(1).Severity == "轻微",
            "AI劳累状态按类别统一成疲劳，不要求玩家使用具体疲劳名称");
        int ellaFatigue = Add(created, "跋涉疲劳", "疲劳", "重度", "ella");
        int disease = Add(created, "患病疲劳", "疾病", "轻微");
        int misleading = Add(created, "疲劳", "体质", "轻微");
        string before = created.Snapshot();

        var rested = InventoryState.Read(ScenarioActions.CompleteLocal(created, scenario, "rest").Snapshot);
        Check(rested.Statuses.All(status => status.Id != 1), "真实试玩中的轻微搬运疲劳经过小屋短休即可消退");
        Check(rested.Status(2).Description == created.Status(2).Description &&
            rested.Status(disease).Description == created.Status(disease).Description && rested.Status(ellaFatigue).Severity == "重度",
            "短休不治疗伤口和疾病，也不替艾拉恢复状态");
        Check(rested.Status(misleading).Severity == "轻微", "仅名字像疲劳的其他类别不按字眼猜测清除");
        Check(created.Snapshot() == before, "恢复在完整候选副本中进行，采用前原存档保持不变");

        var forest = InventoryState.Read(before);
        forest.SetScene(new() { Location = "狩猎林地", PresentCharacters = ["player"], Summary = "在林地停下来。" });
        int severe = 1;
        forest.Status(severe).Name = "跋涉疲劳"; forest.Status(severe).Category = " 疲劳 "; forest.Status(severe).Severity = " 严重 ";
        int energy = Add(forest, "长途赶路后的困倦", "精力", "中等");
        var forestRest = InventoryState.Read(ScenarioActions.CompleteLocal(forest, scenario, "rest").Snapshot);
        Check(forestRest.Status(severe).Severity == "中度" && forestRest.Status(energy).Severity == "轻度",
            "旧档中的空白和严重度别名可归一为等级，普通地点短休准确降低一级");
        var secondRest = InventoryState.Read(ScenarioActions.CompleteLocal(forestRest, scenario, "rest").Snapshot);
        Check(secondRest.Status(severe).Severity == "轻度" && secondRest.Statuses.All(status => status.Id != energy),
            "连续短休按剩余等级逐级恢复，不把中重度疲劳一次清除");

        var atHome = InventoryState.Read(forest.Snapshot());
        atHome.SetScene(scenario.InitialScene);
        var slept = InventoryState.Read(ScenarioActions.CompleteLocal(atHome, scenario, "sleep").Snapshot);
        Check(slept.Statuses.All(status => status.Id != severe && status.Id != energy) && slept.Statuses.Any(status => status.Id == 2),
            "小屋入眠按基础与加成消除三级疲劳，包扎未愈伤势仍保留");
        Check(InventoryState.Read(slept.Snapshot()).Snapshot() == slept.Snapshot(),
            "恢复结果通过原有存档完整往返，无需重建或丢弃旧人物状态");

        var legacy = ScenarioFixture.Ella;
        legacy.FatigueRules.Enabled = false;
        legacy.RecoveryRules["rest"].StatusNames = ["旧白名单里的另一个名字"];
        Check(InventoryState.Read(ScenarioActions.CompleteLocal(created, legacy, "rest").Snapshot).Statuses.All(status => status.Id != 1),
            "兼容加载旧名称配置，但恢复不再依赖任何名称白名单");

        var unknownState = InventoryState.Read(before);
        unknownState.Status(1).Severity = "尚未判明";
        var unknownRest = InventoryState.Read(ScenarioActions.CompleteLocal(unknownState, scenario, "rest").Snapshot);
        Check(unknownRest.Status(1).Severity == "尚未判明", "未知严重度保守保留，不因统一疲劳而自动清除");

        // An incorrectly broad content rule still cannot make sleep heal injury/disease.
        scenario.RecoveryRules["sleep"].Categories = ["疲劳", "精力", "伤势", "疾病", "伤病"];
        var guarded = InventoryState.Read(ScenarioActions.CompleteLocal(created, scenario, "sleep").Snapshot);
        Check(guarded.Status(2).Description == created.Status(2).Description && guarded.Status(disease).Description == created.Status(disease).Description,
            "即使恢复配置误包含伤病类别，休息入眠也不能把伤口或疾病治好");
    }
}
