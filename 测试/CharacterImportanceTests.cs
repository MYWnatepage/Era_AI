using System.Text.Json;
using System.Text.Json.Nodes;
using MinorShift.Emuera.NativeAI;

internal static class CharacterImportanceTests
{
    private static void Check(bool ok, string message)
    { if (!ok) throw new Exception(message); Console.WriteLine("通过：" + message); }
    private static void Reject(Action action, string message)
    {
        try { action(); }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException) { Check(true, message); return; }
        throw new Exception("未拒绝：" + message);
    }
    internal static void Run()
    {
        var scenario = ScenarioFixture.Ella;
        var state = new InventoryState();
        state.BindOwners(scenario.Owners); state.BindCharacters(scenario.Characters); state.BindWorld(scenario);
        state.BindCharacterImportance(scenario); state.SetScene(scenario.InitialScene);
        Check(state.ImportanceOf("player") == "major" && state.ImportanceOf("ella") == "major",
            "当前剧本从major_characters登记主角和艾拉为主要人物");
        string full = "罗莎是村落集市的盐商，围着蓝围巾，说话干脆，与主角相识。" + new string('详', 500);
        state.Characters["npc_1"] = state.Owners["npc_1"] = "罗莎";
        state.DynamicCharacters["npc_1"] = new() { Name = "罗莎", Description = full, CreatedAt = "第3天" };
        state.CharacterLocations["npc_1"] = state.Scene!.Location;
        state.Scene.PresentCharacters.Add("npc_1");
        state.NextCharacterId = 2;
        state.Statuses.Add(new() { Id = 1, Character = "npc_1", Name = "左手食指浅伤", Category = "伤势", Severity = "轻微",
            Description = "左手食指侧面的浅伤已经包扎，按压疼痛，尚未痊愈。", Effects = "握物时有些疼痛。", CreatedAt = "第3天", UpdatedAt = "第3天" });
        state.NextStatusId = 2;
        state.BindCharacterImportance(scenario);
        string original = state.Snapshot();
        using (var cards = JsonDocument.Parse(JsonSerializer.Serialize(state.CharacterPromptCards(new HashSet<string> { "npc_1" }, "问罗莎盐价"), InventoryState.Json)))
            Check(cards.RootElement[0].GetProperty("importance").GetString() == "minor" &&
                cards.RootElement[0].GetProperty("description").GetString()!.Length <= 120 && state.DynamicCharacters["npc_1"].Description == full,
                "次要人物只发送120字内摘要，本地完整人物说明保持原样");
        Check(CharacterOverview.Detail(state, "npc_1", 0)[28] == full && CharacterOverview.Detail(state, "npc_1", 0)[29] == "次要人物" &&
            CharacterOverview.Detail(state, "npc_1", 0)[31] == "1" && CharacterOverview.Detail(state, "ella", 0)[31] == "0",
            "人物详情显示层级和完整说明，仅本局新增人物提供升降级入口");
        Check(state.Snapshot() == original, "人物摘要和只读详情不修改保存资料或状态版本");
        int beforeRevision = state.Revision;
        state.SetCharacterImportance("npc_1", "major");
        using (var cards = JsonDocument.Parse(JsonSerializer.Serialize(state.CharacterPromptCards(new HashSet<string> { "npc_1" }, "问罗莎身世"), InventoryState.Json)))
            Check(cards.RootElement[0].GetProperty("importance").GetString() == "major" &&
                cards.RootElement[0].GetProperty("description").GetString() == full && state.Revision == beforeRevision + 1,
                "玩家提升常驻人物后提供较完整摘要，状态版本更新一次");
        var restored = InventoryState.Read(state.Snapshot());
        restored.BindOwners(scenario.Owners); restored.BindCharacters(scenario.Characters); restored.BindWorld(scenario); restored.BindCharacterImportance(scenario);
        Check(restored.ImportanceOf("npc_1") == "major" && restored.DynamicCharacters["npc_1"].Description == full &&
            restored.Status(1).Description == state.Status(1).Description && restored.CharacterLocations["npc_1"] == state.CharacterLocations["npc_1"],
            "人物升格随存档恢复与重新绑定保留，不丢失说明、伤势或位置");
        int sameRevision = restored.Revision;
        restored.SetCharacterImportance("npc_1", "major");
        Check(restored.Revision == sameRevision, "重复选择相同人物层级不制造额外状态修改");
        restored.SetCharacterImportance("npc_1", "minor");
        Check(restored.DynamicCharacters["npc_1"].Description == full && restored.Status(1).Description == state.Status(1).Description,
            "降为次要人物仅改变发送深度，不删除长期资料与有效伤势");
        string snapshot = restored.Snapshot();
        Reject(() => restored.SetCharacterImportance("npc_1", "villain"), "人物等级仅允许主要或次要");
        Reject(() => restored.SetCharacterImportance("player", "minor"), "主角等初始人物等级由剧本设定，不能经新增人物接口改写");
        Reject(() => restored.SetCharacterImportance("ghost", "major"), "不能给未登记人物设置层级");
        Check(restored.Snapshot() == snapshot, "非法等级操作不改变当前人物资料与版本");

        var legacy = JsonNode.Parse(snapshot)!; legacy.AsObject().Remove("character_importance");
        var old = InventoryState.Read(legacy.ToJsonString()); old.BindCharacterImportance(scenario);
        Check(old.ImportanceOf("player") == "major" && old.ImportanceOf("ella") == "major" && old.ImportanceOf("npc_1") == "minor" &&
            old.DynamicCharacters["npc_1"].Description == full && old.Status(1).Description == state.Status(1).Description,
            "旧存档缺少人物等级时补默认值，不重建或裁剪已保存资料");
        var corrupted = JsonNode.Parse(snapshot)!;
        corrupted["character_importance"]!["npc_1"] = "villain";
        Reject(() => InventoryState.Read(corrupted.ToJsonString()), "无效存档层级明确拒绝读取而非静默重置");

        var other = new InventoryState();
        other.BindCharacters(new Dictionary<string, string> { ["player"] = "旅客", ["guide"] = "向导" });
        other.BindCharacterImportance(new ScenarioDefinition { MajorCharacters = ["guide"] });
        Check(other.ImportanceOf("guide") == "major", "主要人物由各剧本声明，框架不硬编码艾拉或其他名字");
        var invalidScenario = ScenarioFixture.Ella;
        invalidScenario.MajorCharacters = ["not-registered"];
        Reject(invalidScenario.Validate, "剧本主要人物配置必须引用已登记人物");
    }
}
