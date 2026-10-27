using System.Text.Json;
using MinorShift.Emuera.NativeAI;

internal static class StatusSemanticsTests
{
    static void Check(bool ok, string message)
    { if (!ok) throw new Exception(message); Console.WriteLine("通过：" + message); }

    static InventoryState Fresh()
    {
        var scenario = ScenarioFixture.Ella;
        scenario.FatigueRules.Enabled = false; // These fixtures cover pre-score saves and providers.
        var state = new InventoryState(); state.BindOwners(scenario.Owners); state.BindCharacters(scenario.Characters);
        state.BindWorld(scenario); state.SetScene(scenario.InitialScene);
        state.AddInitial(1, "布条", "干净的布条。", "消耗品", "", 2, "player", "");
        return state;
    }

    static int Add(InventoryState state, string name, string severity, string description, string effects = "活动吃力。", string category = "疲劳", string character = "player")
    {
        int id = state.NextStatusId++;
        state.Statuses.Add(new() { Id = id, Character = character, Name = name, Category = category, Severity = severity,
            Description = description, Effects = effects, CreatedAt = "第2天", UpdatedAt = "第3天" });
        return id;
    }

    static object Fatigue(string name, string severity = "轻微", string character = "player") => new
    { op = "create_status", character, name, category = "疲劳", severity, description = "连续劳作后四肢酸乏。", effects = "持续活动吃力。", reason = "本轮实际劳作。" };

    static string Reply(InventoryState state, string turn, object[] ops, object[]? items = null) => JsonSerializer.Serialize(new
    { story = "本轮剧情。", turn_id = turn, base_revision = state.Revision, inventory_ops = items ?? [], state_ops = ops });
    static InventoryReplyResult Apply(InventoryState state, string turn, object[] ops, object[]? items = null) =>
        InventoryReply.Apply(new(state.Snapshot(), turn, GameTime: "第3天 时间2/8"), Reply(state, turn, ops, items));

    internal static void Run()
    {
        var legacyScenario = ScenarioFixture.Ella;
        legacyScenario.FatigueRules.Enabled = false;
        var original = Fresh(); string before = original.Snapshot();
        var first = Apply(original, "tired", [Fatigue("扛完木柴浑身没劲")]);
        var tired = InventoryState.Read(first.Snapshot);
        Check(first.RequiresApproval && tired.Statuses.Single().Name == "疲劳" && tired.Statuses.Single().Category == "疲劳" && original.Snapshot() == before,
            "自然劳累显示名统一为疲劳，变化仍须确认且不提前改原状态");
        int id = tired.Statuses.Single().Id, next = tired.NextStatusId; string created = tired.Status(id).CreatedAt;
        var further = InventoryState.Read(Apply(tired, "tired-more", [Fatigue("走得双腿发沉", "中度")]).Snapshot);
        Check(further.Statuses.Count == 1 && further.Status(id).Severity == "中度" && further.NextStatusId == next && further.Status(id).CreatedAt == created,
            "同一人物再次劳累沿用疲劳原编号，不按同义描述叠加新状态");
        var sameRound = InventoryState.Read(Apply(original, "twice", [Fatigue("搬运劳累"), Fatigue("跋涉乏力", "重度")]).Snapshot);
        Check(sameRound.Statuses.Count == 1 && sameRound.NextStatusId == 2 && sameRound.Status(1).Severity == "重度",
            "同轮两种疲劳说法仍只维护一条当前疲劳与一次编号分配");
        var twoPeople = InventoryState.Read(Apply(original, "two-people", [Fatigue("有些困"), Fatigue("想歇会儿", character: "ella")]).Snapshot);
        Check(twoPeople.Statuses.Count == 2 && twoPeople.Statuses.Select(s => s.Character).Distinct().Count() == 2,
            "不同人物各自拥有独立疲劳，不把艾拉的劳累合到主角名下");

        var legacy = Fresh(); legacy.Revision = 6; legacy.LastTurnId = "legacy-turn";
        int light = Add(legacy, "搬运疲劳", "轻微", "搬木柴后肩膀酸胀。", "肩背活动吃力。");
        Add(legacy, "跋涉疲劳", "严重", "赶路后双腿发沉。", "走路缓慢。");
        int disease = Add(legacy, "患病疲劳", "中度", "生病后的虚弱。", category: "疾病");
        int energy = Add(legacy, "困倦", "中等", "旧包的精力记录。", category: "精力");
        int trait = Add(legacy, "疲劳", "轻微", "另一类别的自定义记录。", category: "体质");
        Add(legacy, "走路累了", "中度", "艾拉走得有些累。", character: "ella");
        int oldNext = legacy.NextStatusId;
        var migrated = InventoryState.Read(legacy.Snapshot());
        Check(migrated.Statuses.Count(s => s.Character == "player" && s.Category == "疲劳") == 1 && migrated.Status(light).Name == "疲劳" &&
            migrated.Status(light).Severity == "严重" && migrated.NextStatusId == oldNext && migrated.Revision == 6 && migrated.LastTurnId == "legacy-turn",
            "旧档重复疲劳合并到最小原编号并保留最高已知程度，不改回合与编号分配进度");
        Check(migrated.Status(light).Description.Contains("肩膀酸胀") && migrated.Status(light).Description.Contains("双腿发沉") &&
            migrated.Status(light).Effects.Contains("肩背") && migrated.Status(light).Effects.Contains("走路缓慢"),
            "旧疲劳的不同原因、描述和影响合并保留");
        Check(migrated.Status(disease).Category == "疾病" && migrated.Status(energy).Category == "精力" && migrated.Status(trait).Category == "体质",
            "疾病名称带疲劳不被按词误归类，旧精力和其他类别保留原类型");
        Check(InventoryState.Read(migrated.Snapshot()).Snapshot() == migrated.Snapshot(), "统一疲劳迁移只发生一次，重复存读稳定");

        var uncertain = Fresh(); int unknown = Add(uncertain, "原因未明的乏力", "尚未判明", "劳累原因和程度尚不明确。");
        Add(uncertain, "搬运疲劳", "重度", "搬运后很疲惫。");
        var conservative = InventoryState.Read(uncertain.Snapshot());
        Check(conservative.Status(unknown).Severity == "尚未判明" &&
            InventoryState.Read(ScenarioActions.CompleteLocal(conservative, legacyScenario, "sleep").Snapshot).Statuses.Any(s => s.Id == unknown),
            "旧档合并包含未知程度时不擅自降级或被一次入眠清除");

        var longLegacy = Fresh();
        int longId = Add(longLegacy, "搬运疲劳", "轻微", new string('甲', 995) + "原文甲尾", new string('丙', 495) + "影响甲尾");
        Add(longLegacy, "跋涉疲劳", "重度", new string('乙', 995) + "原文乙尾", new string('丁', 495) + "影响乙尾");
        var longMerged = InventoryState.Read(longLegacy.Snapshot());
        Check(longMerged.Statuses.Count == 1 && longMerged.FatigueHistory[longId].Count == 2 && longMerged.Status(longId).Description.Length <= 1000 &&
            longMerged.FatigueDetail(longMerged.Status(longId)).Contains("原文甲尾") && longMerged.FatigueDetail(longMerged.Status(longId)).Contains("原文乙尾") &&
            longMerged.FatigueDetail(longMerged.Status(longId)).Contains("影响甲尾") && longMerged.FatigueDetail(longMerged.Status(longId)).Contains("影响乙尾"),
            "超长旧疲劳来源保存在同一存档本地档案，详情完整可见且当前状态不超长");
        var prompt = PromptBuilder.User(new AiRequest("背景", "看看天气", "fixture", "第3天", 1024, 0, "", Inventory: new(longMerged.Snapshot(), "local-archive")), []);
        Check(!prompt.Contains("fatigue_history") && !prompt.Contains("原文甲尾"), "旧疲劳档案不逐轮发送给AI，只发当前疲劳摘要");
        var cured = InventoryState.Read(ScenarioActions.CompleteLocal(longMerged, legacyScenario, "sleep").Snapshot);
        Check(cured.Statuses.Count == 0 && cured.FatigueHistory.Count == 0, "疲劳消退时同步清理旧疲劳补充档案");

        var wound = Fresh(); int woundId = Add(wound, "左手食指划伤", "轻微", "左手食指有浅口，尚未处理。", "碰触疼痛。", "伤势");
        object update = new { op = "update_status", status_id = woundId, character = "player", name = "刚才那处破皮", category = "伤势", severity = "轻微",
            description = "左手食指已经包扎，尚未愈合。", effects = "按压仍会疼痛。", reason = "实际取用布条包扎。" };
        object consume = new { op = "change_quantity", item_id = 1, owner = "player", delta = -1, reason = "包扎用一条布条。" };
        var bandaged = InventoryState.Read(Apply(wound, "wrap", [update], [consume]).Snapshot);
        Check(bandaged.Statuses.Count == 1 && bandaged.Status(woundId).Name == "刚才那处破皮" && bandaged.Status(woundId).Description.Contains("尚未愈合") && bandaged.Quantity(1) == 1,
            "伤势自然换说法仍按原状态编号更新，实际处理才消耗材料且不自动痊愈");
        var consulted = InventoryState.Read(Apply(wound, "ask-only", []).Snapshot);
        Check(consulted.Quantity(1) == 2 && consulted.Status(woundId).Description == wound.Status(woundId).Description,
            "治疗咨询仅给剧情建议时不消耗材料、不修改伤势");
        var invalid = new { op = "update_status", status_id = woundId, character = "player", name = "累了", category = "疲劳", severity = "轻微",
            description = "试图把伤口改成疲劳。", effects = "", reason = "错误类别。" };
        string woundBefore = wound.Snapshot();
        try { Apply(wound, "wrong-kind", [invalid], [consume]); throw new Exception("伤势不能改为疲劳"); }
        catch (InvalidOperationException) { Check(wound.Snapshot() == woundBefore, "伤势不能换成可休息消除的疲劳，失败连同材料消耗原子拒绝"); }
    }
}
