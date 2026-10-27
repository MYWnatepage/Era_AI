using System;
using System.IO;
using System.Text.Json;

namespace MinorShift.Emuera.NativeDisplay;

internal sealed record MusicSettings(bool Enabled = true, int Volume = 50, string CustomPath = "", int BuiltInTrack = 0)
{
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsValid => Volume is >= 0 and <= 100 && CustomPath != null && BuiltInTrack is >= 0 and <= 1;
}

internal sealed class MusicSettingsStore(string path)
{
    public MusicSettings Load()
    {
        try
        {
            var value = File.Exists(path) ? JsonSerializer.Deserialize<MusicSettings>(File.ReadAllText(path)) : null;
            return value?.IsValid == true ? value : new();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { return new(); }
    }
    public bool Save(MusicSettings value)
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
