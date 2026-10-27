using System.Numerics;
using MinorShift.Emuera.NativeAI;
using MinorShift.Emuera.NativeDisplay;

internal static class NativeMapRouteTests
{
    private static void Check(bool value, string message)
    { if (!value) throw new Exception(message); Console.WriteLine("通过：" + message); }
    internal static void Run()
    {
        var scenario = ScenarioFixture.Ella;
        var state = new InventoryState(); state.BindCharacters(scenario.Characters); state.BindWorld(scenario); state.SetScene(scenario.InitialScene);
        string before = state.Snapshot();
        var world = NativeSceneWorld.Build(SceneMapDocument.BuildPayload(state, scenario.World, new DisplayTheme()));
        var route = new NativeMapRoute(world); var ground = new NativeMapGround(world);
        Check(route.Begin() && route.Points.Count == 1 && route.Points[0].X == -25 && route.Points[0].Z == 18,
            "路线始终从存档的当前位置地面开始");
        Check(route.Append(-8, 12) && route.Append(10.25f, 8.75f), "路线终点可以落在任意地面坐标，不吸附到固定地点中心");
        route.Finish();
        Check(route.HasRoute && !route.IsDrawing && !route.IsConfirmed && route.EndPoint is Vector3 end && Math.Abs(end.X - 10.25f) < .001 && Math.Abs(end.Z - 8.75f) < .001,
            "右键绘制完成后保留待确认路线及自由终点");
        Check(route.Points.All(p => ground.TryPoint(p.X, p.Z, out var g) && Math.Abs(p.Y - g.Y - NativeMapRoute.GroundOffset) < .0001f),
            "路线各采样点贴合实际渲染三角地形");
        float[] preview = route.BuildLineVertices(); int version = route.Version;
        Check(preview.Length > 0 && preview.Length % 8 == 0 && preview.All(float.IsFinite), "路线预览与地面箭头生成有效绘图线段");
        Check(route.Confirm() && route.IsConfirmed && route.Version > version && !route.Confirm() && route.BuildLineVertices().Length > preview.Length,
            "确认将虚线变为实线，重复确认不会重新结算");
        Check(!route.Append(12, 10) && route.Clear() && !route.HasRoute && route.EndPoint == null && !route.IsConfirmed,
            "确认后不继续绘制，清除会同时移除路线、箭头与自由终点");
        Check(route.Begin() && !route.Append(float.NaN, 3) && !route.Append(2000, 2000), "无效坐标和地图外的点不会写入路线");
        route.Finish(); Check(!route.HasRoute, "未形成有效线段的手势不留下路线");
        Check(state.Snapshot() == before, "绘制、确认和清除路线均不改变游戏存档、人物位置或时间");
        var unknown = NativeSceneWorld.Build("{\"currentId\":0,\"sites\":[]}");
        Check(!new NativeMapRoute(unknown).Begin(), "当前位置未知时不会假造路线起点");
    }
}
