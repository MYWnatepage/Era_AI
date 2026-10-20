using System;
using System.Drawing;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MinorShift.Emuera.NativeDisplay;

internal sealed record DisplayTheme(int TextRgb = 0xC0C0C0, int BackgroundRgb = 0x000000, int FocusRgb = -1)
{
    [JsonIgnore] public Color TextColor => Color.FromArgb((TextRgb >> 16) & 255, (TextRgb >> 8) & 255, TextRgb & 255);
    [JsonIgnore] public Color BackgroundColor => Color.FromArgb((BackgroundRgb >> 16) & 255, (BackgroundRgb >> 8) & 255, BackgroundRgb & 255);
    [JsonIgnore] public bool IsValid => TextRgb is >= 0 and <= 0xFFFFFF && BackgroundRgb is >= 0 and <= 0xFFFFFF &&
        ColorSafety.Contrast(TextColor, BackgroundColor) >= ColorSafety.MinimumContrast &&
        (FocusRgb == -1 || ColorSafety.ValidFocus(this));
    public DisplayTheme Safe()
    {
        if (TextRgb is < 0 or > 0xFFFFFF || BackgroundRgb is < 0 or > 0xFFFFFF) return new();
        var safe = ColorSafety.Contrast(TextColor, BackgroundColor) >= ColorSafety.MinimumContrast ? this : this with { TextRgb = ColorSafety.Pack(ColorSafety.ReadableFallback(BackgroundColor)) };
        return safe.FocusRgb == -1 || ColorSafety.ValidFocus(safe) ? safe : safe with { FocusRgb = -1 };
    }
}

internal static class ColorSafety
{
    public const double MinimumContrast = 4.5;
    public static int Pack(Color color) => (color.R << 16) | (color.G << 8) | color.B;
    private static double Linear(byte channel)
    {
        double value = channel / 255.0;
        return value <= 0.04045 ? value / 12.92 : Math.Pow((value + 0.055) / 1.055, 2.4);
    }
    public static double Luminance(Color color) => .2126 * Linear(color.R) + .7152 * Linear(color.G) + .0722 * Linear(color.B);
    public static double Contrast(Color a, Color b)
    {
        var first = Luminance(a);
        var second = Luminance(b);
        return (Math.Max(first, second) + .05) / (Math.Min(first, second) + .05);
    }
    public static Color ReadableFallback(Color background) => Contrast(Color.Black, background) >= Contrast(Color.White, background) ? Color.Black : Color.White;
    public static Color FocusColor(DisplayTheme theme) => Color.FromArgb((theme.FocusRgb >> 16) & 255, (theme.FocusRgb >> 8) & 255, theme.FocusRgb & 255);
    public static bool ValidFocus(DisplayTheme theme)
    {
        var color = FocusColor(theme);
        return theme.FocusRgb is >= 0 and <= 0xFFFFFF && Contrast(color, theme.BackgroundColor) >= MinimumContrast &&
            Math.Abs(color.R - theme.TextColor.R) + Math.Abs(color.G - theme.TextColor.G) + Math.Abs(color.B - theme.TextColor.B) > 80;
    }
    public static Color Selection(DisplayTheme theme)
    {
        if (ValidFocus(theme)) return FocusColor(theme);
        foreach (var color in new[] { Color.Yellow, Color.FromArgb(0, 80, 170), Color.Cyan, Color.Black, Color.White })
            if (Contrast(color, theme.BackgroundColor) >= MinimumContrast && Math.Abs(color.R - theme.TextColor.R) + Math.Abs(color.G - theme.TextColor.G) + Math.Abs(color.B - theme.TextColor.B) > 80)
                return color;
        return theme.TextColor;
    }
}

internal sealed class DisplayThemeStore(string directory)
{
    private readonly string path = Path.Combine(directory, "display-settings.json");
    public DisplayTheme Load(DisplayTheme fallback)
    {
        try
        {
            return File.Exists(path) ? (JsonSerializer.Deserialize<DisplayTheme>(File.ReadAllText(path)) ?? fallback).Safe() : fallback.Safe();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { return fallback.Safe(); }
    }
    public void Save(DisplayTheme theme)
    {
        if (!theme.IsValid) throw new ArgumentException("文字与背景过于接近，请将对比度调整到 4.5∶1 以上。");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(theme, new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
        File.Move(path + ".tmp", path, true);
    }
}
