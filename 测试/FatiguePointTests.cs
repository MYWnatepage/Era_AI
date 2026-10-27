using System.Numerics;
using MinorShift.Emuera.NativeAI;
using MinorShift.Emuera.NativeDisplay;

internal static class FatiguePointTests
{
    private static void Check(bool ok, string message)
    { if (!ok) throw new Exception(message); Console.WriteLine("通过：" + message); }
    private static void Reject(Action action, string message)
    {
        try { action(); } catch (Exception error) when (error is ArgumentException or InvalidOperationException)
        { Check(true, message); return; }
        throw new Exception(message);
    }
    private static InventoryState Fresh(ScenarioDefinition scenario)
    {
        var state = new InventoryState(); state.BindOwners(scenario.Owners); state.BindCharacters(scenario.Characters);
        state.BindWorld(scenario); state.SetScene(scenario.InitialScene); state.BindFatigue(scenario);
        return state;
    }
    private static int Add(InventoryState state, string name, string severity, string category = "疲劳", string character = "player")
    {
        int id = state.NextStatusId++;
        state.Statuses.Add(new() { Id = id, Character = character, Name = name, Category = category, Severity = severity,
            Description = name + "的原有详细情况。", Effects = "原有影响。", CreatedAt = "第2天", UpdatedAt = "第3天" });
        return id;
    }
    private static InventoryReplyResult Reply(InventoryState source, ScenarioDefinition scenario, params object[] operations)
    {
        string turn = Guid.NewGuid().ToString("N");
        string text = System.Text.Json.JsonSerializer.Serialize(new { story = "你确认了眼下的身体情况。", turn_id = turn,
            base_revision = source.Revision, inventory_ops = Array.Empty<object>(), state_ops = operations, effort = "none" });
        return InventoryReply.Apply(new(source.Snapshot(), turn, Scenario: scenario), text);
    }
    private static object EnergyUpdate(int id, string category = "精力") => new { op = "update_status", status_id = id,
        character = "player", name = "旧旅途乏力", category, severity = "轻微", description = "走动后的酸乏已有缓解。",
        effects = "长时间活动仍需留意。", reason = "本轮确认旧状态已缓解。" };

    internal static void Run()
    {
        var scenario = ScenarioFixture.Ella; scenario.Validate();
        var state = Fresh(scenario);
        Check(state.FatiguePoints == 0 && FatigueSystem.Penalty(state, scenario) == 0 && state.Statuses.Count == 0,
            "新局以0疲劳点开始，精力良好无骰点减值或多余状态");
        int wound = Add(state, "左手擦伤", "轻微", "伤势");
        int npc = Add(state, "旅途劳累", "中度", character: "ella");
        string injury = state.Status(wound).Description;
        FatigueSystem.Change(state, scenario, 24.99m, "测试活动");
        Check(FatigueSystem.Level(state.FatiguePoints!.Value) == 0 && FatigueSystem.Penalty(state, scenario) == 0 &&
            state.Statuses.All(s => s.Character != "player" || s.Category != "疲劳"), "不足25点不产生疲劳程度或骰点惩罚");
        FatigueSystem.Change(state, scenario, .01m, "继续活动");
        var fatigue = state.Statuses.Single(s => s.Character == "player" && s.Category == "疲劳"); int fatigueId = fatigue.Id;
        Check(state.FatiguePoints == 25 && fatigue.Severity == "轻微" && FatigueSystem.Penalty(state, scenario) == 2,
            "25点进入轻微疲劳，骰点减2且只维护一条派生状态");
        FatigueSystem.Change(state, scenario, 25, "继续活动");
        Check(state.FatiguePoints == 50 && state.Status(fatigueId).Severity == "中度" && FatigueSystem.Penalty(state, scenario) == 4,
            "50点进入中度疲劳，保留原状态编号并减4骰点");
        FatigueSystem.Change(state, scenario, 25, "继续活动");
        Check(state.FatiguePoints == 75 && state.Status(fatigueId).Severity == "重度" && FatigueSystem.Penalty(state, scenario) == 6,
            "75点进入重度疲劳，保留原编号并减6骰点");
        FatigueSystem.Change(state, scenario, 999, "长时间活动");
        Check(state.FatiguePoints == 100 && state.Revision == 0, "疲劳最多100，领域计算不额外递增回合版本");
        ScenarioActions.Ensure(state, scenario, "move", 3, 1, 0);
        ScenarioActions.EnsureLocation(state, scenario, "rest");
        Check(true, "重度疲劳仍可移动和休息，不会被锁死在野外");
        FatigueSystem.Change(state, scenario, -999, "充分恢复");
        Check(state.FatiguePoints == 0 && state.Statuses.All(s => s.Id != fatigueId) && state.Status(wound).Description == injury &&
            state.Status(npc).Severity == "中度", "恢复最低为0，派生疲劳消退且伤势与其他人物状态不变");
        string noneBefore = state.Snapshot();
        FatigueSystem.ApplyEffort(state, scenario, "none");
        Check(state.Snapshot() == noneBefore, "交谈等无体力消耗行动不增加疲劳或改写回合");
        FatigueSystem.ApplyEffort(state, scenario, "light"); FatigueSystem.ApplyEffort(state, scenario, "moderate"); FatigueSystem.ApplyEffort(state, scenario, "heavy");
        Check(state.FatiguePoints == 35, "自由行动强度通过本地规则换算5、10、20点，不由AI随意给数值");
        Reject(() => FatigueSystem.ApplyEffort(state, scenario, "invented"), "未知活动强度不默默结算");
        Check(FatigueSystem.HuntCost(scenario) == 25, "当前剧本狩猎增加25疲劳点");

        state.FatiguePoints = 80; FatigueSystem.SyncStatus(state);
        string beforeRest = state.Snapshot();
        var atHome = InventoryState.Read(ScenarioActions.CompleteLocal(state, scenario, "rest").Snapshot);
        Check(atHome.FatiguePoints == 55 && state.Snapshot() == beforeRest, "小屋短休恢复25点，仅生成候选，不重复执行旧等级恢复");
        var slept = InventoryState.Read(ScenarioActions.CompleteLocal(state, scenario, "sleep").Snapshot);
        Check(slept.FatiguePoints == 0 && slept.Status(wound).Description == injury && slept.Status(npc).Severity == "中度",
            "小屋入眠恢复80点，伤病和场外人物记录保留");
        var outdoors = InventoryState.Read(beforeRest);
        outdoors.SetScene(new() { Location = "狩猎林地", PresentCharacters = ["player"], Summary = "在林地停下。" });
        var outsideRest = InventoryState.Read(ScenarioActions.CompleteLocal(outdoors, scenario, "rest").Snapshot);
        Check(outsideRest.FatiguePoints == 65 && ScenarioActions.RecoveryHint(outdoors, scenario, "rest").Contains("15 点"),
            "普通地点短休恢复15点，菜单显示点数而非旧等级");
        Check(SceneActionPresentation.BenefitsForScene(state, scenario, state.KnownScenes.Single(s => s.Id == 1))
            .SequenceEqual(["[休息：疲劳恢复 +10 点]", "[入眠：疲劳恢复 +20 点]"]), "地图特殊增益与实际额外10、20点恢复一致");

        var legacyEnergy = Fresh(scenario); FatigueSystem.Change(legacyEnergy, scenario, 80, "继续活动");
        int energyId = Add(legacyEnergy, "旧旅途乏力", "重度", "精力");
        int energyNpc = Add(legacyEnergy, "艾拉的旧劳累", "中度", "精力", "ella");
        int energyWound = Add(legacyEnergy, "左手旧擦伤", "轻微", "伤势");
        string energyBefore = legacyEnergy.Snapshot();
        var energyRest = InventoryState.Read(ScenarioActions.CompleteLocal(legacyEnergy, scenario, "rest").Snapshot);
        Check(energyRest.FatiguePoints == 55 && energyRest.Status(energyId).Severity == "轻度" &&
            energyRest.Statuses.Single(s => s.Character == "player" && s.Category == "疲劳").Severity == "中度" &&
            legacyEnergy.Snapshot() == energyBefore,
            "数字疲劳只恢复25点，既有精力独立按旧规则恢复2级，不再对派生疲劳重复恢复");
        Check(energyRest.Status(energyNpc).Severity == "中度" &&
            energyRest.Status(energyWound).Description == legacyEnergy.Status(energyWound).Description,
            "旧精力兼容恢复不改变NPC或伤势");
        var energySleep = InventoryState.Read(ScenarioActions.CompleteLocal(legacyEnergy, scenario, "sleep").Snapshot);
        Check(energySleep.FatiguePoints == 0 && energySleep.Statuses.All(s => s.Id != energyId) &&
            energySleep.Status(energyNpc).Severity == "中度", "小屋入眠可结束旧精力状态，点数仍只恢复一次80点");
        var energyUpdated = InventoryState.Read(Reply(legacyEnergy, scenario, EnergyUpdate(energyId)).Snapshot);
        Check(energyUpdated.Status(energyId).Severity == "轻微" && energyUpdated.FatiguePoints == 80,
            "AI可沿原编号和类别维护既有主角精力记录，不直接改变疲劳点");
        var energyRemoved = InventoryState.Read(Reply(legacyEnergy, scenario,
            new { op = "remove_status", status_id = energyId, character = "player", reason = "旧劳累已确实消退。" }).Snapshot);
        Check(energyRemoved.Statuses.All(s => s.Id != energyId) && energyRemoved.FatiguePoints == 80,
            "AI可移除已消退的旧精力记录而不清除量化疲劳");
        Reject(() => Reply(legacyEnergy, scenario, new { op = "create_status", character = "player", name = "另一次劳累",
            category = "精力", severity = "轻微", description = "活动后稍感劳累。", effects = "", reason = "本轮活动。" }),
            "数字模式仍禁止新建主角精力记录绕过唯一疲劳点来源");
        Reject(() => Reply(legacyEnergy, scenario, EnergyUpdate(energyId, "疲劳")), "旧精力不能改为程序疲劳");
        Reject(() => Reply(legacyEnergy, scenario, EnergyUpdate(energyId, "伤势")), "旧精力不能改成其他状态类别");
        int numericId = legacyEnergy.Statuses.Single(s => s.Character == "player" && s.Category == "疲劳").Id;
        Reject(() => Reply(legacyEnergy, scenario, EnergyUpdate(numericId)), "程序疲劳不能改为旧精力");
        Reject(() => Reply(legacyEnergy, scenario, EnergyUpdate(energyWound)), "伤势不能改为旧精力绕过恢复限制");
        Check(legacyEnergy.Snapshot() == energyBefore, "旧精力预览和被拒提案均不改写原进度");

        var walking = Fresh(scenario);
        var world = NativeSceneWorld.Build(SceneMapDocument.BuildPayload(walking, scenario.World, new DisplayTheme(), scenario));
        var route = new NativeMapRoute(world); Check(route.Begin() && route.Append(-20, 16), "数值疲劳测试路线贴地且从当前位置开始");
        route.Finish(); route.Confirm();
        var travel = new NativeMapTravel(walking, scenario, world, 1, 0);
        string beforeWalk = walking.Snapshot(); Check(travel.Quote(route.Points).Allowed && walking.Snapshot() == beforeWalk, "路线预览不提前增加疲劳");
        var walked = InventoryState.Read(travel.Apply(travel.Request(route.Points)).Snapshot);
        Check(Math.Abs(walked.FatiguePoints!.Value - FatigueSystem.MovementCost(route.LengthMetres, scenario)) < .000001m &&
            walking.Snapshot() == beforeWalk, "移动按真实距离每500米5点累计，小路段也保留小数且只更新候选");
        Check(InventoryState.Read(walked.Snapshot()).FatiguePoints == walked.FatiguePoints, "精确疲劳点随存档保存读取，不在加载时重复加点");

        var oldScenario = ScenarioFixture.Ella; oldScenario.FatigueRules.Enabled = false;
        var legacy = Fresh(oldScenario); legacy.Revision = 8; legacy.LastTurnId = "old-turn";
        int oldId = Add(legacy, "搬运疲劳", "轻微"); Add(legacy, "跋涉疲劳", "重度");
        legacy = InventoryState.Read(legacy.Snapshot()); legacy.BindFatigue(scenario);
        Check(legacy.FatiguePoints == 75 && legacy.Status(oldId).Severity == "重度" && legacy.Revision == 8 && legacy.LastTurnId == "old-turn" &&
            legacy.FatigueDetail(legacy.Status(oldId)).Contains("搬运疲劳") && legacy.FatigueDetail(legacy.Status(oldId)).Contains("跋涉疲劳"),
            "旧疲劳按最高程度折算75点，保留原编号、描述档案及回合，不重置进度");
        string upgraded = legacy.Snapshot(); legacy.BindFatigue(scenario);
        Check(legacy.Snapshot() == upgraded, "旧档疲劳折算仅执行一次，重复绑定不会重复加点或归档");
        var uncertain = Fresh(oldScenario); int uncertainId = Add(uncertain, "原因未明的疲劳", "尚未判明"); uncertain.BindFatigue(scenario);
        Check(uncertain.FatiguePoints == 75 && uncertain.FatigueDetail(uncertain.Status(uncertainId)).Contains("原因未明"),
            "旧未知疲劳保守折算并保留原文，不按零疲劳清除");
        Check(Fresh(oldScenario).FatiguePoints == null, "未启用疲劳点的旧剧本保留原状态机制");
        var invalid = Fresh(scenario); invalid.FatiguePoints = 101;
        Reject(() => invalid.Snapshot(), "损坏存档的越界疲劳点明确拒绝");
        var invalidRules = ScenarioFixture.Ella; invalidRules.FatigueRules.DicePenalties = [0, 4, 2, 6];
        Reject(invalidRules.Validate, "疲劳骰点惩罚不得随程度加重反而降低");
    }
}
