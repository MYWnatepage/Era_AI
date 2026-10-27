using System.Drawing;
using System.Text.Json;
using MinorShift.Emuera.NativeAI;
using MinorShift.Emuera.NativeDisplay;

internal static class SceneMapTests
{
    private static void Check(bool value, string message)
    { if (!value) throw new Exception(message); Console.WriteLine("通过：" + message); }
    internal static void Run()
    {
        var scenario = ScenarioFixture.Ella;
        var state = new InventoryState();
        state.BindCharacters(scenario.Characters); state.BindWorld(scenario); state.SetScene(scenario.InitialScene);
        state.SceneConditions[state.Scene!.Location] = new() { ["壁炉"] = "燃烧中" };
        string original = state.Snapshot();
        int closed = 0;
        var page = new SceneMapPage(state, scenario.World, () => closed++);
        using var font = new Font("Microsoft YaHei UI", 22, GraphicsUnit.Pixel);
        var theme = new DisplayTheme(0xDCDCDC, 0x0F0F12, 0xFFFF00);
        foreach (var size in new Size[] { new(1920, 1000), new(1280, 720), new(640, 480) })
        {
            using var bitmap = new Bitmap(size.Width, size.Height);
            using var g = Graphics.FromImage(bitmap);
            page.Draw(g, new(Point.Empty, size), font, theme, 32);
            Check(page.Targets.Count == 5 && page.Targets.All(h => new Rectangle(Point.Empty, size).Contains(h.Bounds)),
                $"场景地图适配{size.Width}×{size.Height}，地点命中区均在窗口内");
            foreach (var target in page.Targets)
                Check(page.Surface!.HitTest(Point.Round(target.Center)) == target.Id, $"地点{target.Id}按实际多边形表面响应悬停");
            Check(page.Surface!.ReliefRange > .25 && page.Surface.TriangleCount > 1500,
                "地图具有连续高度变化及经过投影的三角地形网格");
            var forest = page.Surface.Regions.Single(r => r.Id == 4);
            var cabin = page.Surface.Regions.Single(r => r.Id == 1);
            var stream = page.Surface.Regions.Single(r => r.Id == 3);
            Check(forest.Bounds.Width * forest.Bounds.Height > cabin.Bounds.Width * cabin.Bounds.Height * 2 &&
                stream.Bounds.Height > cabin.Bounds.Height * 3, "林地、小屋和贯穿河流具有明显不同的区域尺度");
            var emptyCorners = new[] { cabin.Bounds.Location, new Point(cabin.Bounds.Right - 1, cabin.Bounds.Top),
                new Point(cabin.Bounds.Left, cabin.Bounds.Bottom - 1), new Point(cabin.Bounds.Right - 1, cabin.Bounds.Bottom - 1) };
            Check(emptyCorners.Any(p => !cabin.Contains(p) && page.Surface.HitTest(p) != cabin.Id),
                "不规则区域外接矩形的空白角落不会触发该地点焦点");
            int selected = page.SelectedId;
            var point = page.Targets.Single(h => h.Id != selected && h.Id == 5).Center;
            page.Move(Point.Round(point));
            Check(page.HoveredId == 5 && page.SelectedName == "村落集市", "悬停切换焦点及下方地点详情");
            page.Draw(g, new(Point.Empty, size), font, theme, 32);
            page.MouseLeave();
            Check(page.HoveredId == null && page.SelectedId == 5, "移出地图取消悬停高亮并保留最后查看的地点介绍");
            page.Select(1);
        }
        Check(state.Snapshot() == original && page.CurrentLocation == "森林边的小屋", "地图构建、悬停、翻页和浏览不移动人物或修改快照");
        var custom = new DisplayTheme(0xE7B27D, 0x141018, 0x7AD7FA).Safe();
        Check(SceneMapTerrain.Tint(custom, 0) == custom.BackgroundColor && SceneMapTerrain.Tint(custom, 1) == custom.TextColor &&
            SceneMapTerrain.Tint(custom, 1, true) == ColorSafety.Selection(custom), "地形、地点及焦点颜色直接从项目配色派生");
        using (var bitmap = new Bitmap(640, 480))
        using (var g = Graphics.FromImage(bitmap))
        {
            var geometry = page.Surface;
            page.Draw(g, new(0, 0, 640, 480), font, custom, 32);
            Check(ReferenceEquals(geometry, page.Surface), "切换颜色复用地形几何并即时重绘，不重建或改变本局地形");
        }
        var legacy = InventoryState.Read(original);
        foreach (var card in legacy.KnownScenes) card.Map = null;
        string legacySnapshot = legacy.Snapshot();
        var legacyPage = new SceneMapPage(legacy, scenario.World, () => { });
        using (var bitmap = new Bitmap(1280, 720))
        using (var g = Graphics.FromImage(bitmap)) legacyPage.Draw(g, new(0, 0, 1280, 720), font, theme, 32);
        Check(legacyPage.Targets.Count == 5 && legacy.Snapshot() == legacySnapshot, "旧档只读取可选地图布局，不覆盖旧档描述、设施或人物位置");
        var many = new InventoryState();
        for (int i = 1; i <= 19; i++) many.KnownScenes.Add(new() { Id = i, Name = "动态地点" + i, Description = new string('长', 1100) });
        many.NextSceneId = 20;
        var manyPage = new SceneMapPage(many, null, () => { });
        using (var bitmap = new Bitmap(640, 480))
        using (var g = Graphics.FromImage(bitmap))
        {
            manyPage.Draw(g, new(0, 0, 640, 480), font, theme, 32);
            Check(manyPage.PageCount == 3 && manyPage.Targets.Count == 8, "新增地点没有坐标时自动排布，超过八个地点分页");
            manyPage.ChangePage(999); manyPage.Draw(g, new(0, 0, 640, 480), font, theme, 32);
            Check(manyPage.Targets.Select(h => h.Id).SequenceEqual(new[] { 17, 18, 19 }), "最后一页只展示实际已知地点");
            manyPage.Wheel(manyPage.DetailBounds.Location, -120);
            Check(manyPage.ScrollOffset > 0, "长地点描述可以独立滚动");
        }
        Check(new SceneMapPage(new InventoryState(), null, () => { }).SelectedId == 0, "空地图和未知当前位置不虚构开场地点");
        var longText = InventoryState.Read(original);
        longText.KnownScenes[0].Description = new string('长', 1100);
        var shortText = InventoryState.Read(original);
        shortText.KnownScenes[0].Description = "简短地点介绍。";
        var longPage = new SceneMapPage(longText, scenario.World, () => { });
        var shortPage = new SceneMapPage(shortText, scenario.World, () => { });
        using (var a = new Bitmap(640, 480))
        using (var b = new Bitmap(640, 480))
        {
            using (var g = Graphics.FromImage(a)) longPage.Draw(g, new(0, 0, 640, 480), font, theme, 32);
            using (var g = Graphics.FromImage(b)) shortPage.Draw(g, new(0, 0, 640, 480), font, theme, 32);
            bool same = true;
            for (int y = longPage.ReturnBounds.Top; y < 480; y++)
                for (int x = 0; x < 640; x++) same &= a.GetPixel(x, y) == b.GetPixel(x, y);
            Check(same, "长描述严格裁切在介绍区，不覆盖底部返回按钮");
        }
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(state.WorldPrompt("看看这里"), InventoryState.Json));
        Check(!doc.RootElement.GetProperty("scene_cards")[0].TryGetProperty("map", out _), "绘图坐标和图形不随剧情请求发送给AI");
        page.Close(); page.Close();
        Check(closed == 1, "地图返回只触发一次，避免重复返回穿透");
        var copy = scenario.World.Scenes[0].Copy();
        if (copy.Map != null) { double x = scenario.World.Scenes[0].Map!.X; copy.Map.X = .99; Check(scenario.World.Scenes[0].Map!.X == x, "地图位置在场景副本之间独立保存"); }
        try { new SceneMapPosition { X = double.NaN, Y = .5 }.Validate(); throw new Exception("未拒绝非法地图坐标"); }
        catch (ArgumentException) { Check(true, "绘图坐标拒绝非有限数值"); }
    }
}
