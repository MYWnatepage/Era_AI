using System.Text.Json;
using System.Numerics;
using MinorShift.Emuera.NativeAI;
using MinorShift.Emuera.NativeDisplay;

internal static class NativeMapTravelTests
{
    private static void Check(bool value, string message)
    { if (!value) throw new Exception(message); Console.WriteLine("通过：" + message); }
    private static void Reject(Action action, string message)
    {
        try { action(); } catch (Exception e) when (e is InvalidOperationException or ArgumentException or JsonException)
        { Console.WriteLine("通过：" + message); return; }
        throw new Exception(message);
    }
    internal static void Run()
    {
        var scenario = ScenarioFixture.Ella;
        var state = new InventoryState(); state.BindCharacters(scenario.Characters); state.BindWorld(scenario); state.SetScene(scenario.InitialScene);
        NativeSceneWorld World(InventoryState s) => NativeSceneWorld.Build(SceneMapDocument.BuildPayload(s, scenario.World, new DisplayTheme(), scenario));
        NativeMapRoute Route(NativeSceneWorld w, float x, float z)
        { var r = new NativeMapRoute(w); Check(r.Begin() && r.Append(x, z), "测试移动路线能够贴地绘制"); r.Finish(); r.Confirm(); return r; }
        var world = World(state); var route = Route(world, -20, 16);
        var legacyUnknown = new InventoryState(); legacyUnknown.BindCharacters(scenario.Characters); legacyUnknown.BindWorld(scenario);
        string legacyBefore = legacyUnknown.Snapshot();
        var unknownWorld = World(legacyUnknown);
        Check(unknownWorld.CurrentMarker == null && !new NativeMapRoute(unknownWorld).Begin() && legacyUnknown.Snapshot() == legacyBefore,
            "旧档完全未知位置时地图可构建但不伪造起点，画线安全拒绝且不强塞开场位置");
        legacyUnknown.CharacterLocations["player"] = "森林边的小屋";
        Check(World(legacyUnknown).CurrentMarker?.Name == "森林边的小屋" && legacyUnknown.Scene == null,
            "缺场景旧档地图可使用已登记所在地作参考，浏览不改写存档场景");
        var travel = new NativeMapTravel(state, scenario, world, 1, 0);
        string before = state.Snapshot(), request = travel.Request(route.Points);
        Check(travel.Quote(route.Points).Allowed, "短路线可预览行动点且不改变状态");
        var result = travel.Apply(request); var moved = InventoryState.Read(result.Snapshot);
        Check(!new[] { "绘制", "坐标", "程序", "选定的路线" }.Any(word => (result.Narrative + moved.Scene!.Summary).Contains(word, StringComparison.Ordinal)) &&
            result.Changes.Contains("移动消耗"), "移动叙事与场景近况使用自然文字，行动点信息仍清楚保留");
        Check(result.TimeCost == 0 && moved.MovementRemainder > 0 && moved.MovementRemainder < 1 &&
            Math.Abs((double)moved.MovementRemainder - route.LengthMetres / 500.0) < .00001,
            "不足500米按实际距离累计小数行动点，不强制扣一整格");
        Check(moved.PlayerPosition is { X: -20, Z: 16 } && moved.CharacterLocations["ella"] == "森林边的小屋" && state.Snapshot() == before,
            "保存精确终点，仅移动主角，候选结算不改原状态");
        var nextWorld = World(moved); var next = Route(nextWorld, 30, 10);
        Check(next.Points[0].X == -20 && next.Points[0].Z == 16, "再次画路线从上次实际终点开始，不回到地点中心");
        var second = new NativeMapTravel(moved, scenario, nextWorld, 1, 0).Apply(new NativeMapTravel(moved, scenario, nextWorld, 1, 0).Request(next.Points));
        var twice = InventoryState.Read(second.Snapshot);
        Check(second.TimeCost >= 1 && Math.Abs(second.TimeCost + (double)twice.MovementRemainder -
            (double)moved.MovementRemainder - next.LengthMetres / 500.0) < .00001,
            "连续移动保留不足一格的消耗，累计达到整格才推进时间");
        Reject(() => new NativeMapTravel(moved, scenario, nextWorld, 1, 0).Apply(request), "已使用或过期路线不能重复结算");
        var low = InventoryState.Read(before); low.MovementRemainder = .99m;
        var lowTravel = new NativeMapTravel(low, scenario, World(low), 1, 7);
        Check(!lowTravel.Quote(route.Points).Allowed, "剩余行动点不足时预览禁止开始移动");
        string lowBefore = low.Snapshot();
        Reject(() => lowTravel.Apply(lowTravel.Request(route.Points)), "执行时再次校验行动点，不足则整条路线不生效");
        Check(low.Snapshot() == lowBefore, "失败移动不扣点、不改位置和物品");
        var altered = JsonSerializer.Deserialize<TravelRouteRequest>(request, InventoryState.Json)!;
        altered.Points[0].X += 1;
        Reject(() => travel.Apply(JsonSerializer.Serialize(altered, InventoryState.Json)), "路线起点必须与存档实际坐标一致");
        altered = JsonSerializer.Deserialize<TravelRouteRequest>(request, InventoryState.Json)!;
        altered.Points[^1].X = 2000;
        Reject(() => travel.Apply(JsonSerializer.Serialize(altered, InventoryState.Json)), "地图外或不连续的路线不会被采用");
        var wildRoute = Route(world, -70, 70);
        var wild = InventoryState.Read(travel.Apply(travel.Request(wildRoute.Points)).Snapshot);
        Check(wild.PlayerPosition?.IsWilderness == true && wild.Scene!.Location == "野外", "固定地点区域以外的自由终点记为野外，不吸附到小屋");
        int scenes = wild.KnownScenes.Count; wild.BindWorld(scenario);
        Check(wild.KnownScenes.Count == scenes && ScenarioActions.LocationReason(wild, scenario, "rest") == "" &&
            ScenarioActions.LocationReason(wild, scenario, "sleep") != "", "野外可以休息和继续移动，但不获得小屋入眠权限或虚构新地点");
        var wildWorld = World(wild);
        Check(wildWorld.CurrentMarker is { X: -70, Z: 70 } && new NativeMapRoute(wildWorld).Begin(), "野外坐标随存档恢复，当前位置光标和新路线起点一致");
        var homeRoute = Route(wildWorld, -25, 18);
        var returnTravel = new NativeMapTravel(wild, scenario, wildWorld, 1, (int)(wildRoute.LengthMetres / 500));
        var home = InventoryState.Read(returnTravel.Apply(returnTravel.Request(homeRoute.Points)).Snapshot);
        Check(home.PlayerPosition?.IsWilderness == false && home.Scene!.Location == "森林边的小屋" &&
            ScenarioActions.LocationReason(home, scenario, "sleep") == "" && home.Scene.PresentCharacters.Contains("ella"),
            "进入小屋实际区域后恢复小屋行动权限，并显示留在小屋的艾拉");
        var sleepy = InventoryState.Read(before); sleepy.MovementRemainder = .4m;
        Check(InventoryState.Read(ScenarioActions.CompleteLocal(sleepy, scenario, "sleep").Snapshot).MovementRemainder == 0,
            "入眠结束当天时清除当日不足一格的已用行动点");
    }
}
