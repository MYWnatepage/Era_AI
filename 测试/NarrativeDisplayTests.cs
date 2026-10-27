using System.Drawing;
using MinorShift.Emuera.NativeDisplay;

internal static class NarrativeDisplayTests
{
    static void Check(bool ok, string message) { if (!ok) throw new Exception(message); Console.WriteLine("通过：" + message); }
    internal static void Run()
    {
        string folder = Path.Combine(Path.GetTempPath(), "era-narrative-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder); string path = Path.Combine(folder, "settings.json");
        try
        {
            var store = new NarrativeDisplayStore(path);
            Check(!store.Load().Gradual && store.Load().CharactersPerSecond == 30, "无设置时保持非流式的一起显示，默认速度30字/秒");
            Check(store.Save(new(true, 73)) && new NarrativeDisplayStore(path).Load() == new NarrativeDisplaySettings(true, 73), "显示模式和速度独立持久保存");
            Check(!store.Save(new(true, 0)) && store.Load().CharactersPerSecond == 73, "越界速度不覆盖已保存设置");
            File.WriteAllText(path, "{坏配置"); Check(store.Load() == new NarrativeDisplaySettings(), "损坏显示配置安全回退");
            var unicode = new NarrativePlayback("甲🍂e\u0301乙\r\n丙", 2, true);
            Check(unicode.Advance(.49) == 0 && unicode.Advance(.5) == 1 && unicode.Advance(1) == 3 && unicode.Advance(1.5) == 5,
                "显示速度按完整文字元素计数，不拆开表情或组合字符");
            Check(unicode.Text == "甲🍂e\u0301乙\n丙" && unicode.Advance(0) == 5, "换行统一且时钟回退不让已显示文字消失");
            unicode.RevealAll(); Check(unicode.Finished && unicode.Advance(0) == unicode.Text.Length, "立即补全幂等，不重复播放");
            Check(new NarrativePlayback("完整剧情", 1, false).Finished && new NarrativePlayback("", 1, true).Finished, "一起显示及空文本不进入等待");
            var slow = new NarrativePlayback("一二三", 1, true); Check(slow.Advance(1) == 1 && slow.Advance(3) == 3, "最低速度按每秒一字推进");
            NarrativeDisplaySettings saved = new(true, 30); int closes = 0;
            var editor = new NarrativeDisplayEditor(saved, (value, persist) => { saved = value; return ""; }, () => closes++);
            using var image = new Bitmap(1280, 720); using var graphics = Graphics.FromImage(image); using var font = new Font("Microsoft YaHei UI", 24, GraphicsUnit.Pixel);
            editor.Draw(graphics, new(0, 0, 1280, 720), font, new DisplayTheme(), 32);
            editor.MouseDown(new(editor.Track.Right, editor.Track.Top + 10)); editor.MouseUp();
            Check(saved.CharactersPerSecond == 120, "滑块右端为120字/秒");
            editor.MouseDown(new(editor.Track.Left, editor.Track.Top + 10)); editor.MouseUp();
            Check(saved.CharactersPerSecond == 1, "滑块左端为1字/秒");
            editor.Submit("abc"); Check(saved.CharactersPerSecond == 1 && editor.Message.Length > 0, "无效输入不会改速度");
            editor.Mode(false); editor.Speed(90); Check(!saved.Gradual && saved.CharactersPerSecond == 1, "一起显示时滑块不可调，保留先前速度");
            editor.Mode(true); editor.Submit("60"); Check(saved.Gradual && saved.CharactersPerSecond == 60, "重新选择逐字模式可输入速度");
            editor.MouseDown(new(editor.StreamingBounds.Left + 2, editor.StreamingBounds.Top + 2));
            editor.Draw(graphics, new(0, 0, 1280, 720), font, new DisplayTheme(), 32);
            editor.Speed(120); editor.Mode(false); editor.Submit("10");
            Check(editor.StreamingPreview && editor.Track.IsEmpty && saved == new NarrativeDisplaySettings(true, 60),
                "流式占位页隐藏未实现控件，不改变非流式显示配置");
            editor.MouseDown(new(editor.NonStreamingBounds.Left + 2, editor.NonStreamingBounds.Top + 2));
            editor.Draw(graphics, new(0, 0, 1280, 720), font, new DisplayTheme(), 32);
            Check(!editor.StreamingPreview && editor.Track.Width > 0 && saved.CharactersPerSecond == 60, "切回非流式恢复原显示方式和速度");
            editor.Submit("0"); Check(closes == 1, "输入0正常返回，不修改剧情");
        }
        finally { if (File.Exists(path)) File.Delete(path); if (File.Exists(path + ".tmp")) File.Delete(path + ".tmp"); Directory.Delete(folder); }
    }
}
