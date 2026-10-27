using System.Drawing;
using MinorShift.Emuera.NativeDisplay;

internal static class MusicSettingsTests
{
    public static void Run()
    {
        static void Check(bool ok, string name)
        {
            if (!ok) throw new Exception("音乐设置测试失败：" + name);
            Console.WriteLine("通过：" + name);
        }
        var root = Path.Combine(Path.GetTempPath(), "EraAI-music-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, "music-settings.json");
            var store = new MusicSettingsStore(path);
            Check(store.Load() == new MusicSettings(), "音乐默认开启，音量 50%");
            Check(store.Save(new(false, 0, "C:/music/我的音乐.mp3")) && store.Load() == new MusicSettings(false, 0, "C:/music/我的音乐.mp3"), "关闭、静音与自定义中文路径独立持久化");
            Check(!store.Save(new(true, 101)) && store.Load().Volume == 0, "越界音量不覆盖配置");
            File.WriteAllText(path, "{\"Enabled\":false,\"Volume\":30,\"CustomPath\":\"\"}");
            Check(store.Load() == new MusicSettings(false, 30), "旧版音乐配置保留开关和音量，并默认选择第一首");
            Check(store.Save(new(false, 30, "", 1)) && store.Load().BuiltInTrack == 1, "第二首内置曲目持久化");
            Check(!store.Save(new(true, 50, "", 2)) && store.Load().BuiltInTrack == 1, "无效内置曲目不覆盖设置");
            File.WriteAllText(path, "{broken");
            Check(store.Load() == new MusicSettings(), "损坏配置安全恢复默认");
            int preview = -1, saved = -1;
            bool done = false;
            var editor = new MusicVolumeEditor(50, (value, save) => { preview = value; if (save) saved = value; return ""; }, () => done = true);
            editor.Submit("101");
            Check(editor.Value == 50 && preview == -1, "数字输入拒绝越界");
            editor.Submit("0");
            Check(editor.Value == 0 && saved == 0 && !done, "数字 0 表示静音，不误返回");
            editor.Submit("73%");
            Check(editor.Value == 73 && saved == 73, "数字音量支持百分号");
            using var bitmap = new Bitmap(1024, 640);
            using var graphics = Graphics.FromImage(bitmap);
            using var font = new Font("Microsoft YaHei UI", 24, GraphicsUnit.Pixel);
            editor.Draw(graphics, new Rectangle(0, 0, 1024, 640), font, new(), 32);
            if (Environment.GetEnvironmentVariable("ERA_AI_TEST_PREVIEW") is string imagePath) bitmap.Save(imagePath);
            editor.MouseDown(new(editor.Track.Right, editor.Track.Top));
            Check(editor.Value == 100 && saved == 73, "滑块点击即时预览，拖动期间不反复写入磁盘");
            editor.Move(new(editor.Track.Left - 40, editor.Track.Top)); editor.MouseUp();
            Check(editor.Value == 0 && saved == 0, "滑块越界钳制到 0%，松开保存");
            var iconBounds = MusicSpeakerButton.Bounds(new(0, 0, 1024, 640), 32);
            Check(iconBounds.Right == 1012 && iconBounds.Top == 12, "喇叭图标固定在右上角并避开滚动条");
            graphics.Clear(Color.Magenta);
            MusicSpeakerButton.Draw(graphics, iconBounds, new(), false, false);
            Check(bitmap.GetPixel(0, 0).ToArgb() == Color.Magenta.ToArgb(), "图标绘制不覆盖正文");
            using var mutedImage = new Bitmap(1024, 640);
            using var mutedGraphics = Graphics.FromImage(mutedImage);
            MusicSpeakerButton.Draw(mutedGraphics, iconBounds, new(), true, true);
            Check(MusicSpeakerButton.Bounds(new(0, 0, 640, 360), 80).Right == 628, "调整窗口与字号后图标位置保持正确");
            editor.Close();
            Check(done, "关闭音量页后返回设置");
            var failure = new MusicVolumeEditor(50, (_, _) => "保存失败", () => throw new Exception("不应退出"));
            failure.Submit("60"); failure.Close();
            Check(failure.Value == 50 && failure.Message.Length > 0, "保存失败可见且保留数值和页面");
        }
        finally { Directory.Delete(root, true); }
    }
}
