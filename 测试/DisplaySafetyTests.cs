using System.Drawing;
using System.Text.Json;
using MinorShift.Emuera.NativeDisplay;

internal static class DisplaySafetyTests
{
    public static void Run()
    {
        static void Check(bool ok, string label)
        {
            if (!ok) throw new Exception("配色测试失败：" + label);
            Console.WriteLine("通过：" + label);
        }
        Check(Math.Abs(ColorSafety.Contrast(Color.Black, Color.White) - 21) < 1e-8, "黑白对比度为 21∶1");
        Check(ColorSafety.Contrast(Color.White, Color.White) == 1, "同色对比度为 1∶1");
        Check(!new DisplayTheme(0x121212, 0x151515).IsValid, "接近的深色不能保存");
        Check(!new DisplayTheme(0xFAFAFA, 0xFFFFFF).IsValid, "接近的浅色不能保存");
        Check(new DisplayTheme(0x767676, 0xFFFFFF).IsValid, "白底灰字阈值上侧通过");
        Check(!new DisplayTheme(0x777777, 0xFFFFFF).IsValid, "白底灰字阈值下侧不四舍五入放行");
        Check(!new DisplayTheme(-1, 0).IsValid && !new DisplayTheme(0x1000000, 0).IsValid, "RGB 越界保护");
        var random = new Random(42);
        for (int i = 0; i < 5000; i++)
        {
            var theme = new DisplayTheme(random.Next(0x1000000), random.Next(0x1000000)).Safe();
            if (!theme.IsValid || ColorSafety.Contrast(ColorSafety.Selection(theme), theme.BackgroundColor) < 4.5)
                throw new Exception("配色或选中按钮安全保护失效。");
        }
        Check(true, "5000 组颜色的启动修复与选中按钮对比度保护");
        var directory = Path.Combine(Path.GetTempPath(), "EraAI-display-" + Guid.NewGuid().ToString("N"));
        var store = new DisplayThemeStore(directory);
        var saved = new DisplayTheme(0x302B25, 0xF4EBDD);
        store.Save(saved);
        Check(store.Load(new()) == saved, "安全配色保存与重新加载");
        try { store.Save(new(0, 0)); throw new Exception("错误保存了同色组合。"); }
        catch (ArgumentException) { Check(store.Load(new()) == saved, "拒绝危险配色且保留原设置"); }
        var file = Path.Combine(directory, "display-settings.json");
        File.WriteAllText(file, JsonSerializer.Serialize(new DisplayTheme(0, 0)));
        Check(store.Load(new()).IsValid, "手工写入危险设置时自动修复");
        File.WriteAllText(file, "损坏的配置");
        Check(store.Load(saved) == saved, "配置损坏时回退到安全设置");
    }
}
