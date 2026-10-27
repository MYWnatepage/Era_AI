using System.Text.Json.Nodes;
using MinorShift.Emuera.NativeAI;

internal static class SceneDescriptionMigrationTests
{
    private static void Check(bool value, string message)
    { if (!value) throw new Exception(message); Console.WriteLine("通过：" + message); }

    internal static void Run()
    {
        var scenario = ScenarioFixture.Ella;
        var initial = scenario.World;
        Check(initial.DescriptionMigrations.Count == 5 && initial.Scenes.All(scene =>
            !new[] { "本局", "剧本", "程序", "实际剧情" }.Any(word => scene.Description.Contains(word, StringComparison.Ordinal))),
            "初始地点说明使用自然场景描述，旧说明迁移由剧本资料登记");
        var state = new InventoryState();
        state.BindCharacters(scenario.Characters); state.BindWorld(scenario); state.SetScene(scenario.InitialScene);
        foreach (var card in state.KnownScenes)
        {
            card.Description = initial.DescriptionMigrations.Single(rule => rule.SceneName == card.Name).OldDescriptions[0];
            card.Rules = initial.DescriptionMigrations.Single(rule => rule.SceneName == card.Name).OldRules[0];
        }
        state.SceneConditions[state.KnownScenes[0].Name] = new() { ["壁炉"] = "火已熄灭" };
        state.CharacterLocations["ella"] = "屋外不远的溪边";
        state.Scene!.PresentCharacters.Remove("ella");
        string before = state.Snapshot();
        Check(SceneDescriptionMigrations.Apply(state, initial) && state.KnownScenes.All(card =>
            card.Description == initial.Scenes.Single(template => template.Name == card.Name).Description),
            "旧存档仅将已登记的完整旧地点说明替换为当前自然文案");
        var oldJson = JsonNode.Parse(before)!;
        var newJson = JsonNode.Parse(state.Snapshot())!;
        foreach (var card in oldJson["known_scenes"]!.AsArray()) { card!["description"] = ""; card["rules"] = ""; }
        foreach (var card in newJson["known_scenes"]!.AsArray()) { card!["description"] = ""; card["rules"] = ""; }
        Check(JsonNode.DeepEquals(oldJson, newJson), "地点说明迁移保留人物所在地、设施状态、布局、物品和回合数据");
        Check(!SceneDescriptionMigrations.Apply(state, initial), "地点文案迁移可重复执行且不会重复改动");
        Check(state.KnownScenes.All(card => card.Rules == initial.Scenes.Single(template => template.Name == card.Name).Rules),
            "旧默认地点规则也替换为自然说明，保留地点与行动限制");
        state.Scene!.Summary = "主角沿绘制的路线抵达这里，停留在具体坐标处。";
        state.PlayerPosition = new() { X = -25, Z = 18, Location = state.Scene.Location };
        state.MovementRemainder = .347m;
        string oldSummary = state.Snapshot();
        state.BindWorld(scenario);
        Check(state.Scene.Summary == SceneNarration.Arrival(state.Scene.Location) && state.PlayerPosition.X == -25 &&
            state.MovementRemainder == .347m && state.Revision == 0, "读入旧移动近况时清除程序措辞，不修改精确位置、行动点与版本");
        Check(InventoryState.Read(oldSummary).Scene!.Summary.Contains("具体坐标"), "兼容处理只作用于绑定的副本，不直接改写原存档");
        state.Scene.Summary = "你把旧地图铺在桌上，研究图中的路线和坐标。";
        state.BindWorld(scenario);
        Check(state.Scene.Summary == "你把旧地图铺在桌上，研究图中的路线和坐标。", "真实剧情中的地图和坐标不被粗暴关键词替换");
        state.KnownScenes[0].Description = "木屋的屋顶在风暴后坍塌，壁炉旁积着雨水。";
        string custom = state.KnownScenes[0].Description;
        Check(!SceneDescriptionMigrations.Apply(state, initial) && state.KnownScenes[0].Description == custom,
            "玩家或AI已更新的地点描述不被初始卡覆盖");

        var aliases = new SceneCatalog
        {
            Scenes = [new SceneCard { Id = 1, Name = "新地点名称", Aliases = ["旧地名"], Description = "新版自然描述" }],
            DescriptionMigrations = [new SceneDescriptionMigration { SceneName = "新地点名称", OldDescriptions = ["旧版默认描述"] }]
        };
        aliases.Validate();
        var shifted = new InventoryState { KnownScenes = [new SceneCard { Id = 99, Name = "旧地名", Description = "旧版默认描述" }] };
        Check(SceneDescriptionMigrations.Apply(shifted, aliases) && shifted.KnownScenes[0].Id == 99 && shifted.KnownScenes[0].Description == "新版自然描述",
            "迁移按地点名称或别名匹配，不依赖本局编号与初始编号相同");
        var unrelated = new SceneCatalog { Scenes = [new SceneCard { Id = 1, Name = "另一地点", Description = "另一自然描述" }] };
        Check(!SceneDescriptionMigrations.Apply(shifted, unrelated), "无迁移资料的其他剧本不会继承当前剧本的替换规则");

        bool rejected = false;
        aliases.DescriptionMigrations.Add(new SceneDescriptionMigration { SceneName = "旧地名", OldDescriptions = ["旧版默认描述"] });
        try { aliases.Validate(); } catch (ArgumentException) { rejected = true; }
        Check(rejected, "同一地点重复旧说明的迁移配置会被拒绝");
    }
}
