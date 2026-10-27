using System.Text.Json;
using MinorShift.Emuera.NativeAI;

internal static class ScenarioActionAvailabilityTests
{
    private static void Check(bool value, string message)
    { if (!value) throw new Exception(message); Console.WriteLine("通过：" + message); }
    private static void Reject(Action operation, string message)
    {
        try { operation(); }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException)
        { Check(true, message); return; }
        throw new Exception("未拒绝：" + message);
    }

    internal static void Run()
    {
        var scenario = ScenarioFixture.Ella;
        var state = new InventoryState();
        state.BindOwners(scenario.Owners); state.BindCharacters(scenario.Characters);
        state.BindWorld(scenario); state.SetScene(scenario.InitialScene);
        state.AddInitial(2, "食材", "可供烹饪的食物。", "消耗品", "", 10, "player", "食材");
        state.Statuses.Add(new() { Id = 1, Character = "player", Name = "手指浅伤", Category = "伤势", Severity = "轻微",
            Description = "已经包扎但尚未痊愈。", Effects = "按压时疼痛", CreatedAt = "第1天", UpdatedAt = "第1天" });
        state.NextStatusId = 2;
        string original = state.Snapshot();
        var home = ScenarioActions.Availability(state, scenario, 1, 0, 0);
        Check(home.CanInput && home.CanSleep && !home.CanHunt && home.CanRest && home.HuntReason.Contains("狩猎林地"),
            "小屋显示自由输入、入眠和短休，狩猎不可用且说明允许地点");
        Check(home.Fields().Length == 8 && home.Fields().Take(4).SequenceEqual(["1", "1", "0", "1"]),
            "菜单桥按自由输入、入眠、狩猎、短休顺序提供可用标志和原因");
        Reject(() => ScenarioActions.Ensure(state, scenario, "hunt", 1, 0, 0), "小屋执行入口拒绝狩猎");
        Reject(() => ScenarioActions.EnsureLocation(state, scenario, "hunt"), "原生行动准备与失败回退入口同样拒绝屋内狩猎");
        Check(state.Snapshot() == original, "不可用行动检查不推进时间、不掷骰、不修改物资或人物状态");

        var slept = ScenarioActions.CompleteLocal(state, scenario, "sleep");
        var afterSleep = InventoryState.Read(slept.Snapshot);
        Check(afterSleep.Scene!.Location == state.Scene!.Location && afterSleep.CharacterLocations.SequenceEqual(state.CharacterLocations) &&
            afterSleep.Scene.PresentCharacters.SequenceEqual(state.Scene.PresentCharacters) && afterSleep.Revision == state.Revision + 1 &&
            afterSleep.Quantity(2) == 10 && afterSleep.Status(1).Description == state.Status(1).Description,
            "本地入眠只生成原地候选结果，不治疗伤势、不提前扣食材或移动人物");
        Check(state.Snapshot() == original && slept.Narrative.Contains("梦乡") && slept.Narrative.Contains("艾拉"),
            "采用前原进度不变，入眠文字依据真实同地人物生成");

        foreach (var card in scenario.World.Scenes)
        {
            var at = InventoryState.Read(original);
            at.SetScene(new() { Location = card.Name, PresentCharacters = ["player"], Summary = "主角独自到达。" });
            var available = ScenarioActions.Availability(at, scenario, 3, 2, 0);
            Check(available.CanHunt == (card.Id == 4) && available.CanSleep == (card.Id == 1) && available.CanRest,
                $"地点卡{card.Id}的行动范围由剧本配置决定");
        }
        state.SetScene(new() { Location = "狩猎林地", PresentCharacters = ["player"], Summary = "主角独自来到猎场。" });
        string forest = state.Snapshot();
        Check(ScenarioActions.Availability(state, scenario, 2, 4, 1).CanHunt,
            "剩余时间恰好够四格时可以狩猎");
        Check(!ScenarioActions.Availability(state, scenario, 2, 5, 1).CanHunt &&
            ScenarioActions.Availability(state, scenario, 2, 5, 1).HuntReason.Contains("时间不足"),
            "狩猎不足四格时入口禁用");
        Check(!ScenarioActions.Availability(state, scenario, 2, 0, 2).CanHunt &&
            ScenarioActions.Availability(state, scenario, 2, 0, 2).HuntReason.Contains("已经完成"),
            "每日重复狩猎在掷骰和API前被禁止");
        Reject(() => ScenarioActions.CompleteLocal(state, scenario, "sleep"), "猎场不能通过入眠入口瞬移回家");
        Check(state.Snapshot() == forest, "被拒绝的异地入眠不改任何存档数据");
        var shortRest = ScenarioActions.CompleteLocal(state, scenario, "rest");
        var afterRest = InventoryState.Read(shortRest.Snapshot);
        Check(afterRest.Scene!.Location == "狩猎林地" && afterRest.CharacterLocations["ella"] == "森林边的小屋" &&
            afterRest.Scene.PresentCharacters.SequenceEqual(["player"]) && !shortRest.Narrative.Contains("艾拉") &&
            afterRest.Quantity(2) == 10 && afterRest.Status(1).Description.Contains("尚未痊愈"),
            "短休停留原地，不带回场外人物，不治疗伤势或改变物资");
        Check(InventoryState.Read(afterRest.Snapshot()).Snapshot() == afterRest.Snapshot(), "原地短休结果可完整保存还原");

        var action = scenario.Resolve("hunt", 12);
        Check(action.EndScene == null && !action.Instruction.Contains("返回家中"), "狩猎定义不再写死返回小屋");
        var exchange = new InventoryExchange(forest, "stay-hunt", action, scenario.Id);
        string reply = JsonSerializer.Serialize(new { story = action.Story, turn_id = exchange.TurnId, base_revision = state.Revision,
            inventory_ops = Array.Empty<object>(), state_ops = Array.Empty<object>() });
        var hunted = InventoryState.Read(InventoryReply.Apply(exchange, reply).Snapshot);
        Check(hunted.Scene!.Location == "狩猎林地" && hunted.Quantity(2) == 13 && hunted.CharacterLocations["ella"] == "森林边的小屋",
            "狩猎统一入库且停在原猎场，艾拉留在小屋");

        state.SetScene(new() { Location = "新发现的山洞", PresentCharacters = ["player"], Summary = "发现未知地点。" });
        var unknown = ScenarioActions.Availability(state, scenario, 2, 1, 1);
        Check(!unknown.CanHunt && !unknown.CanSleep && !unknown.CanRest && unknown.CanInput,
            "新发现地点不会自动获得固定行动权限，自由输入仍可用");
        var legacy = InventoryState.Read(original); legacy.Scene = null; legacy.CharacterLocations.Clear();
        Check(!ScenarioActions.Availability(legacy, scenario, 2, 1, 1).CanHunt,
            "旧档当前位置未知时不会假定主角在猎场");

        // Rename the scenario and all scenes to demonstrate that no location name is built into the rules.
        var other = ScenarioFixture.Ella;
        foreach (var card in other.World.Scenes) { card.Name = "外部地点" + card.Id; card.Aliases.Clear(); }
        var otherState = new InventoryState(); otherState.BindCharacters(other.Characters); otherState.BindWorld(other);
        otherState.SetScene(new() { Location = "外部地点4", PresentCharacters = ["player"], Summary = "另一个世界的猎场。" });
        Check(ScenarioActions.Availability(otherState, other, 1, 0, 0).CanHunt,
            "更名后的独立剧本仍按其地点编号执行规则，框架无地点名耦合");
        other.ActionLocations["hunt"] = [999];
        Reject(other.Validate, "行动配置不能引用未登记的地点");
        Reject(() => ScenarioActions.Availability(state, scenario, 1, 8, 0), "拒绝超出当前日范围的时钟");
        Check(ActionRules.CommonAction("入眠", scenario) == 2 && ActionRules.CommonAction("休息", scenario) == 3 &&
            ActionRules.CommonAction("先休息再狩猎", scenario) == 0, "入眠和短休分别识别，复合计划不自动执行固定行动");
    }
}
