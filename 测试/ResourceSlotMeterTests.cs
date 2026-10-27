using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using MinorShift.Emuera.NativeDisplay;

internal static class ResourceSlotMeterTests
{
    private static void Check(bool ok, string message)
    { if (!ok) throw new Exception(message); Console.WriteLine("通过：" + message); }

    internal static void Run()
    {
        var action = new ResourceSlotMeter(5.25m, 8, 8, ResourceMeterKind.ActionPoints, 20);
        Check(Enumerable.Range(0, 8).Select(action.FillAt).SequenceEqual(new float[] { 1, 1, 1, 1, 1, .25f, 0, 0 }),
            "行动点按实际小数填格，5.25点显示五整格及四分之一格");
        var fatigue = new ResourceSlotMeter(43.2m, 100, 10, ResourceMeterKind.Fatigue, 20);
        Check(fatigue.FillAt(3) == 1 && Math.Abs(fatigue.FillAt(4) - .32f) < .00001 && fatigue.FillAt(5) == 0,
            "疲劳每格十点，43.2显示四整格及0.32格");
        Check(action.PlainText == "行动点 5.25/8" && fatigue.PlainText == "疲劳 43.2/100",
            "状态条具有明确的数值备用文本，不依赖方块字体");
        Check(new ResourceSlotMeter(0, 8, 8, ResourceMeterKind.ActionPoints, 20).FillAt(0) == 0 &&
            new ResourceSlotMeter(8, 8, 8, ResourceMeterKind.ActionPoints, 20).FillAt(7) == 1 &&
            new ResourceSlotMeter(-1, 100, 10, ResourceMeterKind.Fatigue, 20).Value == 0 &&
            new ResourceSlotMeter(101, 100, 10, ResourceMeterKind.Fatigue, 20).Value == 100,
            "空条、满条及边界数值保持正确，不多画或少画格子");

        var actionBands = new[] { 8m, 4m, 2m, 0m }.Select(v => new ResourceSlotMeter(v, 8, 8, ResourceMeterKind.ActionPoints, 20).Band).ToArray();
        Check(actionBands.SequenceEqual(new[] { ResourceMeterBand.Healthy, ResourceMeterBand.Caution, ResourceMeterBand.Danger, ResourceMeterBand.Danger }),
            "行动点颜色按剩余比例切换，半数和四分之一边界准确");
        var fatigueBands = new[] { 0m, 24.999m, 25m, 49.999m, 50m, 74.999m, 75m, 100m }
            .Select(v => new ResourceSlotMeter(v, 100, 10, ResourceMeterKind.Fatigue, 20).Band).ToArray();
        Check(fatigueBands.SequenceEqual(new[] { ResourceMeterBand.Healthy, ResourceMeterBand.Healthy, ResourceMeterBand.Caution, ResourceMeterBand.Caution,
            ResourceMeterBand.Strained, ResourceMeterBand.Strained, ResourceMeterBand.Danger, ResourceMeterBand.Danger }),
            "疲劳状态在25、50、75点切换颜色，不反向表示剩余体力");

        DisplayTheme[] themes = [new(), new(0x202020, 0xFFFFFF), new(0xECECEC, 0x171E2A), new(0x302B25, 0xF4EBDD), new(0xFFFFFF, 0x777777)];
        foreach (var theme in themes)
        {
            var colors = new[] { 0m, 25m, 50m, 75m }.Select(v => new ResourceSlotMeter(v, 100, 10, ResourceMeterKind.Fatigue, 20).ColorFor(theme)).ToArray();
            Check(colors.All(color => ColorSafety.Contrast(color, theme.Safe().BackgroundColor) >= ColorSafety.MinimumContrast) &&
                colors.Select(color => color.ToArgb()).Distinct().Count() == 4,
                "不同状态在黑、白和自定义背景上均保持可读且各自有色差：" + theme.BackgroundRgb.ToString("X6"));
        }

        foreach (float size in new float[] { 12, 18, 32, 48, 96 })
        {
            var meter = new ResourceSlotMeter(5.25m, 8, 8, ResourceMeterKind.ActionPoints, size);
            var polygon = meter.PolygonAt(7, 0, 0);
            Check(polygon.Max(p => p.X) + meter.StrokeWidth / 2 < meter.Width && polygon.Min(p => p.X) >= 0,
                "字体变化后状态框完整落在分配宽度内：" + size);
            double full = Area(meter.PolygonAt(5, 0, 0)), part = Area(meter.PolygonAt(5, 0, 0, .25f));
            Check(Math.Abs(part / full - .25) < .00001, "部分格按面积准确填充，斜边不改变比例：" + size);
        }
        foreach (float size in new float[] { 18, 24, 32, 40 })
        {
            float available = Math.Max(128, 320 - Math.Max(70, size * 4) - 24);
            var narrow = new ResourceSlotMeter(35, 100, 10, ResourceMeterKind.Fatigue, size, available);
            Check(narrow.Width <= available + .001f && narrow.Step >= 12 && narrow.Height >= 8,
                "320像素窄窗口仍完整显示十个可辨状态框，标签和数值允许换行：" + size);
        }

        foreach (var theme in themes.Take(2))
        {
            int empty = PaintedPixels(0, theme), half = PaintedPixels(50, theme), full = PaintedPixels(100, theme);
            Check(empty < half && half < full, "实际位图中空格、半格和实心格的填充量依次增加：" + theme.BackgroundRgb.ToString("X6"));
        }
        if (Environment.GetEnvironmentVariable("ERA_RESOURCE_BARS_PREVIEW") is { Length: > 0 } path) RenderPreview(path);
    }

    private static double Area(PointF[] p) => Math.Abs(Enumerable.Range(0, p.Length)
        .Sum(i => (double)p[i].X * p[(i + 1) % p.Length].Y - (double)p[(i + 1) % p.Length].X * p[i].Y)) / 2;

    private static int PaintedPixels(decimal value, DisplayTheme theme)
    {
        using var image = new Bitmap(100, 80);
        using (var graph = Graphics.FromImage(image))
        {
            graph.Clear(theme.BackgroundColor);
            graph.SmoothingMode = SmoothingMode.HighSpeed;
            var mode = graph.SmoothingMode;
            new ResourceSlotMeter(value, 100, 1, ResourceMeterKind.Fatigue, 24).Draw(graph, 10, 10, 18, theme);
            Check(graph.SmoothingMode == mode, "状态条绘制后恢复画布设置，不影响相邻文字");
        }
        int count = 0, background = theme.BackgroundColor.ToArgb();
        for (int y = 0; y < image.Height; y++)
        for (int x = 0; x < image.Width; x++) if (image.GetPixel(x, y).ToArgb() != background) count++;
        return count;
    }

    private static void RenderPreview(string path)
    {
        path = Path.GetFullPath(path); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var image = new Bitmap(1040, 570);
        using var graph = Graphics.FromImage(image);
        using var font = new Font("Microsoft YaHei", 20, FontStyle.Regular, GraphicsUnit.Pixel);
        using var heading = new Font("Microsoft YaHei", 22, FontStyle.Regular, GraphicsUnit.Pixel);
        DisplayTheme[] themes = [new(0xDDDDDD, 0x101012), new(0x202020, 0xFFFFFF)];
        for (int col = 0; col < themes.Length; col++)
        {
            DisplayTheme theme = themes[col]; int left = col * 520;
            using var background = new SolidBrush(theme.BackgroundColor); graph.FillRectangle(background, left, 0, 520, 570);
            using var brush = new SolidBrush(theme.TextColor);
            graph.DrawString(col == 0 ? "深色背景" : "浅色背景", heading, brush, left + 24, 18);
            for (int row = 0; row < 8; row++)
            {
                var kind = row < 4 ? ResourceMeterKind.ActionPoints : ResourceMeterKind.Fatigue;
                decimal value = new decimal[] { 8, 5.25m, 3, 1.5m, 0, 35, 60, 90 }[row];
                decimal max = row < 4 ? 8 : 100; int slots = row < 4 ? 8 : 10;
                float top = 76 + row * 57;
                graph.DrawString(row < 4 ? "行动点" : "疲劳", font, brush, left + 24, top);
                var meter = new ResourceSlotMeter(value, max, slots, kind, font.Size);
                meter.Draw(graph, left + 99, top, SlotTextAlignment.Center(graph, font, true), theme);
                graph.DrawString(value.ToString("0.##") + "/" + max, font, brush, left + 110 + meter.Width, top);
            }
        }
        image.Save(path, ImageFormat.Png);
        Console.WriteLine("已保存状态框双主题预览：" + path);
    }
}
