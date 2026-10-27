using MinorShift.Emuera.NativeAI;

internal static class LocationActionTests
{
    private static void Check(bool value, string text)
    { if (!value) throw new Exception(text); Console.WriteLine("通过：" + text); }
    private static void Reject(Action action, string text)
    {
        try { action(); } catch (Exception error) when (error is ArgumentException or InvalidOperationException) { Console.WriteLine("通过：" + text); return; }
        throw new Exception(text);
    }
    private static void Add(InventoryState state, string who, string name, string category, string severity)
    {
        state.Statuses.Add(new CharacterStatus { Id = state.NextStatusId++, Character = who, Name = name, Category = category,
            Severity = severity, Description = name + "尚未恢复", Effects = "仍有影响", CreatedAt = "开始时", UpdatedAt = "开始时" });
    }
    internal static void Run()
    {
        var scenario = ScenarioFixture.Ella;
        scenario.FatigueRules.Enabled = false; // Preserve the compatibility bridge's degree recovery checks.
        scenario.Parameters["move_ticks"] = 1;
        scenario.ActionLocations["trade"] = [5];
        scenario.LocationActions = new()
        {
            ["hunt"] = new() { Label = "狩猎", Hint = "掷骰决定收获" },
            ["trade"] = new() { Label = "交易", Hint = "查看摊位并商议买卖", Input = "查看集市上的货物。" }
        };
        ScenarioRecoveryRule Recovery(int levels) => new()
        {
            Levels = levels, Label = "疲劳恢复", StatusNames = ["疲劳", "困倦"], Categories = ["疲劳"],
            SeverityLevels = new() { ["轻度"] = 1, ["中度"] = 2, ["重度"] = 3 },
            SeverityLabels = ["轻度", "中度", "重度"], LocationBonus = new() { [1] = 1 }
        };
        scenario.RecoveryRules = new() { ["rest"] = Recovery(1), ["sleep"] = Recovery(2) };
        scenario.Validate();
        var state = new InventoryState(); state.BindCharacters(scenario.Characters); state.BindWorld(scenario); state.SetScene(scenario.InitialScene);
        Add(state, "player", "疲劳", "疲劳", "重度");
        Add(state, "player", "手臂擦伤", "伤势", "轻度");
        Add(state, "ella", "疲劳", "疲劳", "重度");
        string before = state.Snapshot();

        var homeRest = ScenarioActions.CompleteLocal(state, scenario, "rest");
        var rested = InventoryState.Read(homeRest.Snapshot);
        Check(rested.Status(1).Severity == "轻度" && homeRest.Changes.Contains("重度 → 轻度"), "小屋休息按基础与环境加成恢复两级疲劳");
        Check(rested.Status(2).Description == state.Status(2).Description && rested.Status(3).Severity == "重度",
            "休息加成不治疗伤病，也不替其他人物恢复状态");
        var slept = InventoryState.Read(ScenarioActions.CompleteLocal(state, scenario, "sleep").Snapshot);
        Check(slept.Statuses.All(s => s.Id != 1) && slept.Statuses.Any(s => s.Id == 2) && slept.Statuses.Any(s => s.Id == 3),
            "小屋入眠的三级恢复可解除主角疲劳，伤势与艾拉状态保留");
        var forest = InventoryState.Read(before); forest.SetScene(new() { Location = "狩猎林地", PresentCharacters = ["player"], Summary = "来到林地" });
        var forestRest = InventoryState.Read(ScenarioActions.CompleteLocal(forest, scenario, "rest").Snapshot);
        Check(forestRest.Status(1).Severity == "中度", "普通地点短休只恢复一级疲劳");
        var unknown = InventoryState.Read(before); unknown.Status(1).Severity = "尚未判明";
        Check(InventoryState.Read(ScenarioActions.CompleteLocal(unknown, scenario, "rest").Snapshot).Status(1).Severity == "尚未判明",
            "未知严重度不会被猜测降级或清除");
        var wrongCategory = InventoryState.Read(before); wrongCategory.Status(1).Category = "疾病";
        Check(InventoryState.Read(ScenarioActions.CompleteLocal(wrongCategory, scenario, "rest").Snapshot).Status(1).Severity == "重度",
            "同名但类别不同的状态不会被疲劳恢复规则误处理");
        scenario.RecoveryRules["rest"].Categories = ["疲劳", "疾病", "伤势"];
        Check(InventoryState.Read(ScenarioActions.CompleteLocal(wrongCategory, scenario, "rest").Snapshot).Status(1).Severity == "重度",
            "即使恢复配置误包含疾病类别，休息也不会把同名疾病治好");
        wrongCategory.Status(1).Category = "伤势";
        Check(InventoryState.Read(ScenarioActions.CompleteLocal(wrongCategory, scenario, "rest").Snapshot).Status(1).Severity == "重度",
            "即使恢复配置误包含伤势类别，休息也不会清除同名伤势");
        scenario.RecoveryRules["rest"].Categories = ["疲劳"];
        Check(state.Snapshot() == before, "本地恢复创建候选副本，不修改原始存档");

        Check(ScenarioActions.MovementTargets(state, scenario).Select(c => c.Id).SequenceEqual([2, 3]), "移动菜单只列已知相邻地点");
        var movement = ScenarioActions.ApplyMove(state, scenario, 2); var moved = InventoryState.Read(movement.Snapshot);
        Check(movement.TimeCost == 1 && moved.Scene!.Location == "林间小路" && moved.CharacterLocations["player"] == "林间小路" &&
            moved.CharacterLocations["ella"] == "森林边的小屋" && moved.Scene.PresentCharacters.SequenceEqual(["player"]),
            "固定移动仅移动主角，耗时一格，场外人物不会自动同行");
        Reject(() => ScenarioActions.ApplyMove(state, scenario, 4), "移动不能越过相邻关系直接抵达远处地点");
        moved.CharacterLocations["ella"] = "村落集市";
        var arrived = InventoryState.Read(ScenarioActions.ApplyMove(moved, scenario, 5).Snapshot);
        Check(arrived.Scene!.PresentCharacters.Contains("ella") && arrived.CharacterLocations["ella"] == "村落集市",
            "抵达地点后显示原本就在当地的人物");
        Check(state.Snapshot() == before, "移动候选不修改原始存档");

        var homeMenu = ScenarioActions.MenuFields(state, scenario, 1, 0, 0);
        Check(homeMenu.Length == 17 && homeMenu.Take(8).SequenceEqual(ScenarioActions.Availability(state, scenario, 1, 0, 0).Fields()) &&
            homeMenu[8].Contains("3") && homeMenu[9].Contains("2") && homeMenu[13] == "1", "扩展菜单保留旧八字段并提供加成和移动权限");
        Check(ScenarioActions.MenuFields(forest, scenario, 1, 0, 0)[10] == "hunt" &&
            ScenarioActions.MenuFields(forest, scenario, 1, 0, 1)[15] == "0", "林地专属狩猎受同一天次数限制");
        Check(ScenarioActions.MenuFields(arrived, scenario, 1, 0, 0)[10] == "trade", "集市显示交易专属行动");
        Reject(() => ScenarioActions.EnsureLocation(forest, scenario, "trade"), "交易入口不能在林地绕过地点限制");
        string[] homeActions = SceneActionPresentation.ForScene(state, scenario, state.KnownScenes.Single(s => s.Id == 1));
        string[] forestActions = SceneActionPresentation.ForScene(state, scenario, state.KnownScenes.Single(s => s.Id == 4));
        Check(homeActions.Length == 0 && forestActions.SequenceEqual(["[狩猎]"]) &&
            SceneActionPresentation.ForScene(state, scenario, state.KnownScenes.Single(s => s.Id == 5)).SequenceEqual(["[交易]"]),
            "地图仅显示带方括号的地点专属行动，不重复通用行动或恢复提示");
        Check(SceneActionPresentation.ForScene(state, null, state.KnownScenes[0]).Length == 0,
            "未登记专属行动时不虚构通用行动作为后备");
        Check(SceneActionPresentation.BenefitsForScene(state, scenario, state.KnownScenes.Single(s => s.Id == 1))
                .SequenceEqual(["[休息：疲劳恢复 +1 级]", "[入眠：疲劳恢复 +1 级]"]) &&
            state.KnownScenes.Where(s => s.Id != 1).All(s => SceneActionPresentation.BenefitsForScene(state, scenario, s).Length == 0),
            "特殊增益仅显示实际地点额外恢复加成，不将基础恢复或专属行动当作增益");
        Check(state.Snapshot() == before, "查看其他地点行动不移动人物或更改存档");
        int huntTicks = scenario.Parameters["hunt_ticks"];
        scenario.Parameters.Remove("hunt_ticks");
        ScenarioActions.Ensure(state, scenario, "move", 1, 0, 0);
        ScenarioActions.Ensure(arrived, scenario, "trade", 1, 0, 0);
        Check(true, "移动与交易不要求无关的狩猎耗时参数");
        Reject(() => ScenarioActions.Ensure(state, scenario, "move", 0, 0, 0), "移动仍拒绝无效日期");
        Reject(() => ScenarioActions.Ensure(arrived, scenario, "trade", 1, scenario.Parameter("ticks_per_day"), 0), "交易仍拒绝无效时间");
        scenario.Parameters["hunt_ticks"] = huntTicks;
        scenario.RecoveryRules["rest"].LocationBonus[999] = 1;
        Reject(scenario.Validate, "休息加成不能引用未登记地点");
    }
}
