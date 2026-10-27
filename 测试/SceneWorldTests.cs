using System.Text.Json;
using MinorShift.Emuera.NativeAI;

internal static class SceneWorldTests
{
    private static void Check(bool value, string message)
    { if (!value) throw new Exception(message); Console.WriteLine("通过：" + message); }
    private static void Reject(Action operation, string message)
    {
        try { operation(); } catch (Exception e) when (e is ArgumentException or InvalidOperationException or JsonException)
        { Check(true, message); return; }
        throw new Exception("未拒绝：" + message);
    }
    public static void Run()
    {
        var scenario = ScenarioFixture.Ella;
        Check(scenario.WorldFile == "场景卡.json" && scenario.World.Scenes.Count == 5 && scenario.World.Scenes[0].Name == "森林边的小屋",
            "剧本独立场景卡按登记的相对文件载入，框架不内置测试剧本地点");
        var state = new InventoryState();
        state.BindCharacters(scenario.Characters); state.BindWorld(scenario); state.SetScene(scenario.InitialScene);
        Check(state.KnownScenes.Count == 5 && state.CharacterLocations["ella"] == "森林边的小屋" && state.CharacterLocations["player"] == "森林边的小屋",
            "开场场景建立人物所在地与本局场景副本");
        string initial = state.Snapshot();
        string Reply(object update, string turn, int revision) => JsonSerializer.Serialize(new
        { story = "你沿着小路走到溪边，艾拉留在原地。", turn_id = turn, base_revision = revision, inventory_ops = Array.Empty<object>(), state_ops = Array.Empty<object>(), scene_update = update, effort = "none" });
        Reject(() => InventoryReply.Apply(new(initial, "world-move"), Reply(new
        { location = "溪边", present_characters = new[] { "player" }, summary = "主角独自来到溪边。", reason = "文字声称已经独自前往溪边。" }, "world-move", 0)),
            "自由输入不能通过地点别名绕过地图移动和距离结算");
        state.SetScene(state.ResolveActionScene(new() { Location = "溪边", PresentCharacters = [], Summary = "主角独自来到溪边。" }));
        var moved = InventoryReply.Apply(new(state.Snapshot(), "world-summary"), Reply(new
        { location = "溪边", present_characters = new[] { "player" }, summary = "主角仍独自站在溪边。", reason = "刷新同一地点摘要。" }, "world-summary", 0));
        state = InventoryState.Read(moved.Snapshot);
        Check(!moved.RequiresApproval && state.Scene!.Location == "屋外不远的溪边" && state.CharacterLocations["player"] == "屋外不远的溪边" &&
            state.CharacterLocations["ella"] == "森林边的小屋" && state.Scene.PresentCharacters.SequenceEqual(["player"]),
            "程序移动后仍兼容四字段同地点场景更新，别名正确映射且不让艾拉自动到场");
        var rest = ScenarioActions.CompleteLocal(state, scenario, "rest");
        var rested = InventoryState.Read(rest.Snapshot);
        Check(rested.Scene!.Location == "屋外不远的溪边" && rested.Scene.PresentCharacters.SequenceEqual(["player"]) &&
            rested.CharacterLocations["ella"] == "森林边的小屋" && rest.Narrative.Contains("独自"),
            "溪边短休只停留原地，不能将自己或艾拉传送回家");
        state.SetScene(scenario.InitialScene);
        string bothHome = state.Snapshot();
        try
        {
            InventoryReply.Apply(new(bothHome, "missing-person"), Reply(new
            { location = state.Scene!.Location, present_characters = new[] { "player" }, summary = "独自留在屋内。", reason = "未交代同地人物去向。" }, "missing-person", state.Revision));
            throw new Exception("未拒绝同地点凭空移除已知人物");
        }
        catch (InvalidOperationException error)
        {
            Check(error.Message.Contains("明确去向") && state.Snapshot() == bothHome, "不能让仍在同地点的人物无去向地从在场名单消失");
        }
        var away = InventoryReply.Apply(new(state.Snapshot(), "ella-away"), Reply(new
        {
            location = "森林边的小屋", present_characters = new[] { "player" }, summary = "主角留在家里，艾拉已到集市。",
            reason = "本轮明确确认艾拉到达集市。", character_locations = new Dictionary<string, string> { ["ella"] = "村落集市" }
        }, "ella-away", state.Revision));
        state = InventoryState.Read(away.Snapshot);
        var homeRest = ScenarioActions.CompleteLocal(state, scenario, "rest");
        state = InventoryState.Read(homeRest.Snapshot);
        Check(state.Scene!.PresentCharacters.SequenceEqual(["player"]) && state.CharacterLocations["ella"] == "村落集市" &&
            homeRest.Narrative.Contains("独自") && !homeRest.Narrative.Contains("艾拉"),
            "明确保存场外人物所在地后，短休不把艾拉从集市瞬移回家，休息文字随在场人物生成");
        state.SetScene(new() { Location = "狩猎林地", PresentCharacters = ["player"], Summary = "主角独自抵达猎场。" });
        var hunt = state.ResolveActionScene(scenario.Resolve("hunt", 12).EndScene);
        Check(hunt == null && state.Scene!.Location == "狩猎林地" && state.CharacterLocations["ella"] == "村落集市",
            "狩猎不再指定返回小屋的结束场景，也不依照静态名单重置人物所在地");
        var explicitGroup = state.ResolveActionScene(new() { Location = "林间小路", PresentCharacters = [], Summary = "同行赶路。" }, ["player", "ella"])!;
        state.SetScene(explicitGroup);
        Check(state.CharacterLocations["ella"] == "林间小路" && state.Scene!.PresentCharacters.Count == 2,
            "显式同行名单可以同时移动指定人物");
        string saved = state.Snapshot();
        var restored = InventoryState.Read(saved); restored.BindWorld(scenario);
        Check(restored.Snapshot() == saved, "读档后完整还原场景卡副本、动态人物所在地和当前位置");
        var editedScenario = ScenarioFixture.Ella;
        editedScenario.World.Scenes[0].Description = "后来改写的初始资料";
        restored.BindWorld(editedScenario);
        Check(!restored.KnownScenes[0].Description.Contains("后来改写"), "修改初始场景卡不会覆盖已开始存档中的场景副本");

        var legacyData = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(initial)!;
        foreach (string key in new[] { "scene", "known_scenes", "next_scene_id", "character_locations", "world_initialized", "scene_conditions" }) legacyData.Remove(key);
        var legacy = InventoryState.Read(JsonSerializer.Serialize(legacyData)); legacy.BindWorld(scenario);
        Check(legacy.Scene == null && legacy.CharacterLocations.Count == 0 && legacy.KnownScenes.Count == 5,
            "旧档只补静态参考卡，未知当前位置与场外人物不会被填成开场地点");
        string unknown = legacy.WorldDisplay();
        Check(unknown.Contains("未知") && unknown.Contains("艾拉：未知"), "世界查看页明确显示旧档未知人物位置");

        using var localPrompt = JsonDocument.Parse(JsonSerializer.Serialize(restored.WorldPrompt("看看周围"), InventoryState.Json));
        var localNames = localPrompt.RootElement.GetProperty("scene_cards").EnumerateArray().Select(c => c.GetProperty("name").GetString()).ToArray();
        Check(localNames.SequenceEqual(["林间小路"]) && localPrompt.RootElement.GetProperty("scene_cards")[0].GetProperty("adjacent")
            .EnumerateArray().Any(n => n.GetString() == "森林边的小屋"),
            "普通回合只提供当前地点描述，相邻地点只列名称");
        restored.SetScene(scenario.InitialScene);
        using var homePrompt = JsonDocument.Parse(JsonSerializer.Serialize(restored.WorldPrompt("检查壁炉"), InventoryState.Json));
        Check(!homePrompt.RootElement.GetProperty("scene_cards").EnumerateArray().Any(c => c.GetProperty("name").GetString() == "村落集市"),
            "未点名且不相邻的远方场景描述不塞进普通回合");
        using var namedPrompt = JsonDocument.Parse(JsonSerializer.Serialize(restored.WorldPrompt("明天去集市"), InventoryState.Json));
        Check(namedPrompt.RootElement.GetProperty("scene_cards").EnumerateArray().Any(c => c.GetProperty("name").GetString() == "村落集市"),
            "玩家点名远方地点时补入相关场景卡");
        string original = restored.Snapshot();
        Reject(() => InventoryReply.Apply(new(original, "bad-place"), Reply(new
        {
            location = "溪边", present_characters = new[] { "player" }, summary = "错误冲突", reason = "测试",
            character_locations = new Dictionary<string, string> { ["player"] = "村落集市" }
        }, "bad-place", restored.Revision)), "拒绝同一个人既在溪边又在集市，原存档不变");
        Reject(() => InventoryReply.Apply(new(original, "bad-character"), Reply(new
        {
            location = "溪边", present_characters = new[] { "player" }, summary = "错误人物", reason = "测试",
            character_locations = new Dictionary<string, string> { ["unknown"] = "村落集市" }
        }, "bad-character", restored.Revision)), "拒绝未登记人物的位置更新");
        Check(restored.Snapshot() == original, "场景更新校验失败不会部分提交人物位置");
        var dynamicReply = InventoryReply.Apply(new(original, "new-place"), Reply(new
        { location = "森林边的小屋", present_characters = new[] { "player" }, summary = "主角仍在小屋，艾拉已到旧哨塔。", reason = "本轮确认艾拉到达场外新地点。",
            character_locations = new Dictionary<string, string> { ["ella"] = "山间旧哨塔" } }, "new-place", restored.Revision));
        var dynamicWorld = InventoryState.Read(dynamicReply.Snapshot);
        Check(dynamicWorld.KnownScenes.Any(c => c.Name == "山间旧哨塔") && dynamicWorld.Scene!.Location == "森林边的小屋" &&
            dynamicWorld.CharacterLocations["player"] == "森林边的小屋" && dynamicWorld.CharacterLocations["ella"] == "山间旧哨塔" &&
            InventoryState.Read(dynamicWorld.Snapshot()).WorldDisplay().Contains("山间旧哨塔"), "合法场外人物移动仍可登记新地点并保存，不能因此把主角一起传送过去");
        var locked = InventoryReply.Apply(new(original, "lock-door"), Reply(new
        {
            location = "森林边的小屋", present_characters = new[] { "player", "ella" }, summary = "两人留在小屋。", reason = "本轮锁门并点燃壁炉。",
            conditions = new Dictionary<string, string> { ["门锁"] = "已锁", ["壁炉"] = "燃烧中" }
        }, "lock-door", restored.Revision));
        var lockedWorld = InventoryState.Read(locked.Snapshot);
        Check(locked.RequiresApproval && locked.Changes.Contains("门锁 → 已锁") && locked.Changes.Contains("壁炉 → 燃烧中") &&
            lockedWorld.SceneConditions["森林边的小屋"]["门锁"] == "已锁", "设施状态独立入账，单独锁门或点火也进入确认预览");
        var unlocked = InventoryReply.Apply(new(lockedWorld.Snapshot(), "unlock-door"), Reply(new
        {
            location = "森林边的小屋", present_characters = new[] { "player", "ella" }, summary = "主角开门。", reason = "本轮只打开房门锁。",
            conditions = new Dictionary<string, string> { ["门锁"] = "未锁" }
        }, "unlock-door", lockedWorld.Revision));
        var unlockedWorld = InventoryState.Read(unlocked.Snapshot);
        Check(unlockedWorld.SceneConditions["森林边的小屋"]["门锁"] == "未锁" && unlockedWorld.SceneConditions["森林边的小屋"]["壁炉"] == "燃烧中",
            "设施变化局部合并，没有重复提到的壁炉状态仍保留");
        var unchangedCondition = InventoryReply.Apply(new(unlockedWorld.Snapshot(), "same-condition"), Reply(new
        {
            location = "森林边的小屋", present_characters = new[] { "player", "ella" }, summary = "门锁仍开着。", reason = "仅确认现状。",
            conditions = new Dictionary<string, string> { ["门锁"] = "未锁" }
        }, "same-condition", unlockedWorld.Revision));
        Check(!unchangedCondition.RequiresApproval, "重复相同设施现状不会制造多余变更确认");
        unlockedWorld.SetScene(new() { Location = "溪边", PresentCharacters = ["player"], Summary = "主角独自来到溪边。" });
        Check(unlockedWorld.SceneConditions["森林边的小屋"]["壁炉"] == "燃烧中" &&
            InventoryState.Read(unlockedWorld.Snapshot()).WorldDisplay().Contains("壁炉 / 燃烧中"), "离开与保存读取不会丢失其他地点的设施状态");
        using var streamPrompt = JsonDocument.Parse(JsonSerializer.Serialize(unlockedWorld.WorldPrompt("看看流水"), InventoryState.Json));
        Check(!streamPrompt.RootElement.GetProperty("scene_cards").EnumerateArray().Any(c => c.GetProperty("conditions").TryGetProperty("壁炉", out _)),
            "不相关远处设施状态不发送到当前溪边回合");
        using var namedHomePrompt = JsonDocument.Parse(JsonSerializer.Serialize(unlockedWorld.WorldPrompt("回小屋看看壁炉"), InventoryState.Json));
        Check(namedHomePrompt.RootElement.GetProperty("scene_cards").EnumerateArray().Any(c => c.GetProperty("name").GetString() == "森林边的小屋" &&
            c.GetProperty("conditions").GetProperty("壁炉").GetString() == "燃烧中"), "点名家中地点时带上已保存设施状态");
        Reject(() => InventoryReply.Apply(new(original, "bad-condition"), Reply(new
        {
            location = "森林边的小屋", present_characters = new[] { "player", "ella" }, summary = "非法状态", reason = "测试",
            conditions = new { 门锁 = new { value = "已锁" } }
        }, "bad-condition", restored.Revision)), "设施状态拒绝嵌套对象，不能借此添加隐藏指令或任意数据");
        Check(restored.Snapshot() == original, "非法设施状态不会部分写入原存档");
        string folder = Path.Combine(Path.GetTempPath(), "era-world-card-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            Reject(() => SceneCatalog.Load(folder, "../outside.json"), "场景卡相对路径不能越出当前剧本文件夹");
            File.WriteAllText(Path.Combine(folder, "场景卡.json"), "{\"format_version\":1,\"scenes\":[{\"id\":1,\"name\":\"测试\",\"adjacent\":[99]}]}");
            Reject(() => SceneCatalog.Load(folder, "场景卡.json"), "场景卡不能连接到未登记地点");
        }
        finally { Directory.Delete(folder, true); }
    }
}
