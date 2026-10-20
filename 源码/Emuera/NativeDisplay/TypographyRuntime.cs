using System;
using System.Drawing;
using System.Drawing.Text;
using System.IO;
using System.Linq;
using System.Text.Json;
using MinorShift.Emuera.GameView;
using MinorShift.Emuera.Runtime.Config;

namespace MinorShift.Emuera.NativeDisplay;

internal sealed record TypographySettings(string FontName, int FontSize, int LineHeight);

internal static class TypographyRuntime
{
    private static readonly string PathName = Path.Combine(Program.ExeDir, "配置", "typography-settings.json");
    private static bool loaded;
    public static TypographySettings Current => new(Config.FontName, Config.FontSize, Config.LineHeight);
    public static void Ensure(EmueraConsole console)
    {
        if (loaded) return;
        loaded = true;
        try
        {
            if (File.Exists(PathName))
            {
                var saved = JsonSerializer.Deserialize<TypographySettings>(File.ReadAllText(PathName));
                if (saved != null && Valid(saved)) Apply(console, saved with { FontName = FontNameResolver.Resolve(saved.FontName) });
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or ArgumentException) { }
    }
    private static bool Installed(string name)
    {
        return FontNameResolver.Resolve(name) != null;
    }
    private static bool Valid(TypographySettings value) => value.FontSize is >= 12 and <= 48 &&
        value.LineHeight >= value.FontSize + 6 && value.LineHeight <= 80 && Installed(value.FontName);
    public static string Update(EmueraConsole console, int mode, string name, long number)
    {
        var value = Current;
        if (mode is 4 or 5)
        {
            if (string.IsNullOrWhiteSpace(name)) return "";
            if (!long.TryParse(name.Trim(), out number)) return "请输入有效的整数，留空则取消。";
            mode = mode == 4 ? 2 : 3;
        }
        if (mode == 1)
        {
            if (string.IsNullOrWhiteSpace(name)) return "";
            name = name.Trim();
            var resolved = FontNameResolver.Resolve(name);
            if (resolved == null) return "未找到这个字体，请输入已安装的字体名称。";
            value = value with { FontName = resolved };
        }
        else if (mode == 2)
        {
            if (number is < 12 or > 48) return "字号范围为 12～48。";
            value = value with { FontSize = (int)number, LineHeight = Math.Max(value.LineHeight, (int)number + 6) };
        }
        else if (mode == 3)
        {
            if (number < value.FontSize + 6 || number > 80) return $"行高范围为 {value.FontSize + 6}～80，需为文字保留足够空间。";
            value = value with { LineHeight = (int)number };
        }
        else return "无效的显示设置操作。";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(PathName)!);
            File.WriteAllText(PathName + ".tmp", JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(PathName + ".tmp", PathName, true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return "保存失败，请检查游戏目录的写入权限。"; }
        Apply(console, value);
        return "";
    }
    private static void Apply(EmueraConsole console, TypographySettings value)
    {
        Config.ApplyTypography(value.FontName, value.FontSize, value.LineHeight);
        console.ApplyTypography();
    }
}
