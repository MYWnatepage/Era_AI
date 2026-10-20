using System.Drawing;
using System.Drawing.Imaging;
using MinorShift.Emuera.NativeAI;
using MinorShift.Emuera.NativeDisplay;

internal static class UsageChartTests
{
    public static void Run()
    {
        static void Check(bool value, string text)
        {
            if (!value) throw new Exception("消耗图表测试失败：" + text);
            Console.WriteLine("通过：" + text);
        }
        var dir = Path.Combine(Path.GetTempPath(), "EraAI-chart-" + Guid.NewGuid().ToString("N"));
        DateTimeOffset today = new(2027, 1, 2, 12, 0, 0, TimeSpan.FromHours(8));
        DateTimeOffset clock = today;
        var store = new AccountUsageStore(dir, () => clock);
        var account = new AiSettings { Mode = "api", BaseUrl = "https://api.deepseek.com", Model = "deepseek-flash", ProtectedKey = SecretProtection.Protect("fake-chart-account-key") };
        foreach (int offset in new[] { -8, -6, -4, -1, 0, 1 })
        {
            clock = today.AddDays(offset);
            store.Record(account, new TokenUsage(1000 + offset, 200, 100), account.Model);
        }
        clock = today;
        var days = store.SevenDays(account);
        Check(days.Length == 7 && days[0].Day == new DateOnly(2026, 12, 27) && days[6].Day == new DateOnly(2027, 1, 2), "包含今天在内连续七天，跨年仍按北京时间排列");
        Check(days.Count(x => x.Record != null) == 4 && days[1].Record == null && days[6].Record!.Input == 1000, "缺少记录保留为空，范围外日期不混入图表");
        Check(store.SevenDays(account with { ProtectedKey = SecretProtection.Protect("another-chart-account") }).All(x => x.Record == null), "图表只读取当前 API 连接的记录");
        int closed = 0;
        var chart = new UsageChartPage(days, () => closed++);
        Check(chart.Selected == 6 && !chart.Tokens, "图表默认查看今天及人民币花费区间");
        Check(chart.Value(1) == null && chart.Details(1).Contains("暂无"), "无记录日期不伪装成零花费");
        chart.Command(2);
        Check(chart.Tokens && chart.Value(6)!.Value.High == 1200, "Token 图使用实际输入加输出总量");
        chart.Step(-100); Check(chart.Selected == 0, "键盘日期选择不会超出最早日期");
        chart.Step(100); Check(chart.Selected == 6, "键盘日期选择不会超出今天");

        // Render representative sample data without touching real user statistics.
        var sample = Enumerable.Range(0, 7).Select(i => new UsagePoint(new DateOnly(2026, 10, 13).AddDays(i),
            new UsageDay { Requests = 1, Input = 500 + i * 80, Output = 100 + i * 30, CostMin = new[] { .006m, .003m, .009m, .007m, .012m, .008m, .010m }[i],
                CostMax = new[] { .010m, .005m, .015m, .011m, .019m, .013m, .016m }[i] })).ToArray();
        string preview = Path.Combine(Path.GetTempPath(), "EraAI-chart-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(preview);
        using var font = new Font("Microsoft YaHei", 24, FontStyle.Regular, GraphicsUnit.Pixel);
        foreach (var (name, theme, width, height) in new[]
        {
            ("dark", new DisplayTheme(0x62DEEC, 0x101012), 1400, 850),
            ("light", new DisplayTheme(0x302B25, 0xF4EBDD), 1000, 650),
            ("small", new DisplayTheme(), 600, 380),
        })
        {
            using var bitmap = new Bitmap(width, height);
            using var graphics = Graphics.FromImage(bitmap);
            var renderer = new UsageChartPage(sample, () => { });
            renderer.Draw(graphics, new Rectangle(0, 0, width, height), font, theme, 30);
            bitmap.Save(Path.Combine(preview, name + ".png"), ImageFormat.Png);
        }
        using (var bitmap = new Bitmap(1000, 650))
        using (var graphics = Graphics.FromImage(bitmap))
        {
            chart.Draw(graphics, new Rectangle(0, 0, 1000, 650), font, new(), 30);
            bitmap.Save(Path.Combine(preview, "tokens.png"), ImageFormat.Png);
            chart.Command(1);
            chart.Draw(graphics, new Rectangle(0, 0, 1000, 650), font, new(), 30);
            bitmap.Save(Path.Combine(preview, "gaps.png"), ImageFormat.Png);
            var unknownDays = Enumerable.Range(0, 7).Select(i => new UsagePoint(new DateOnly(2026, 10, 13).AddDays(i),
                i == 6 ? new UsageDay { Requests = 1, MissingUsage = 1 } : null)).ToArray();
            var empty = new UsageChartPage(unknownDays, () => { });
            Check(empty.Value(6) == null, "缺少用量的日期不绘制虚假的零元点");
            empty.Command(2);
            Check(empty.Value(6) == null, "完全缺少用量的日期不绘制虚假的零 Token 点");
            empty.Draw(graphics, new Rectangle(0, 0, 1000, 650), font, new(), 30);
            bitmap.Save(Path.Combine(preview, "empty.png"), ImageFormat.Png);
        }
        chart.MouseDown(new Point(20, 630)); chart.Command(0);
        Check(closed == 1, "退出图表只触发一次返回操作");
        Console.WriteLine("图表绘制检查已输出至：" + preview);
    }
}
