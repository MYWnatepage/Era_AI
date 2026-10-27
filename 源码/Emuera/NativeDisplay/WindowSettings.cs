using System;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace MinorShift.Emuera.NativeDisplay;

internal sealed record WindowSettings(int Width, int Height, bool Maximized = false)
{
    public bool IsValid => Width is >= 640 and <= 7680 && Height is >= 360 and <= 4320;
    public static bool TryParse(string text, out WindowSettings value)
    {
        value = null;
        var parts = Regex.Split(text.Trim(), @"[xX×,，\s]+");
        if (parts.Length != 2 || !int.TryParse(parts[0], out var w) || !int.TryParse(parts[1], out var h)) return false;
        value = new(w, h);
        return value.IsValid;
    }
}

internal sealed class WindowSettingsStore(string path)
{
    public WindowSettings Load()
    {
        try
        {
            var value = File.Exists(path) ? JsonSerializer.Deserialize<WindowSettings>(File.ReadAllText(path)) : null;
            return value?.IsValid == true ? value : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }
    public bool Save(WindowSettings value)
    {
        if (!value.IsValid) return false;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(value));
            File.Move(path + ".tmp", path, true);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
    }
}
