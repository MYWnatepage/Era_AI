using MinorShift.Emuera.NativeDisplay;

internal static class WindowSettingsTests
{
    public static void Run()
    {
        foreach (var input in new[] { "1280×800", "1280x800", "1280 800", " 1280，800 " })
            if (!WindowSettings.TryParse(input, out var value) || value.Width != 1280 || value.Height != 800)
                throw new Exception("有效窗口尺寸未正确解析。");
        foreach (var input in new[] { "", "1280", "abc", "639 800", "1280 359", "99999999999999 800", "1280.5 800", "1280 800 1", "7681 4320" })
            if (WindowSettings.TryParse(input, out _)) throw new Exception("错误窗口尺寸应被拒绝。");
        var folder = Path.Combine(Path.GetTempPath(), "EraAI-window-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var path = Path.Combine(folder, "window-settings.json");
            var store = new WindowSettingsStore(path);
            if (store.Load() != null) throw new Exception("空配置不应改变窗口。");
            var settings = new WindowSettings(1280, 800, true);
            if (!store.Save(settings) || new WindowSettingsStore(path).Load() != settings) throw new Exception("尺寸和最大化状态未持久化。");
            if (store.Save(new(1, 1)) || store.Load() != settings) throw new Exception("无效尺寸不应覆盖原设置。");
            File.WriteAllText(path, "{bad json");
            if (store.Load() != null) throw new Exception("损坏配置应回退原有窗口配置。");
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
        Console.WriteLine("通过：窗口尺寸校验、保存、重新加载与损坏配置回退。");
    }
}
