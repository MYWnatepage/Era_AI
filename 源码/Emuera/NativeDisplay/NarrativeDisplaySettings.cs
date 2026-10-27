#nullable enable
using System;
using System.Globalization;
using System.IO;
using System.Text.Json;

namespace MinorShift.Emuera.NativeDisplay;

internal sealed record NarrativeDisplaySettings(bool Gradual = false, int CharactersPerSecond = 30)
{
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsValid => CharactersPerSecond is >= 1 and <= 120;
    [System.Text.Json.Serialization.JsonIgnore]
    public string ModeName => Gradual ? "逐字显示" : "一起显示";
}

internal sealed class NarrativeDisplayStore(string path)
{
    internal NarrativeDisplaySettings Load()
    {
        try
        {
            var value = File.Exists(path) ? JsonSerializer.Deserialize<NarrativeDisplaySettings>(File.ReadAllText(path)) : null;
            return value?.IsValid == true ? value : new();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { return new(); }
    }
    internal bool Save(NarrativeDisplaySettings value)
    {
        if (!value.IsValid) return false;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(value));
            File.Move(path + ".tmp", path, true);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
    }
}

// Count visible text elements, keeping emoji and combining marks together.
internal sealed class NarrativePlayback
{
    internal string Text { get; }
    internal int VisibleLength { get; private set; }
    internal bool Finished => VisibleLength == Text.Length;
    private readonly int[] elements;
    private readonly int speed;
    internal NarrativePlayback(string text, int charactersPerSecond, bool gradual)
    {
        if (charactersPerSecond is < 1 or > 120) throw new ArgumentOutOfRangeException(nameof(charactersPerSecond));
        Text = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        elements = StringInfo.ParseCombiningCharacters(Text);
        speed = charactersPerSecond;
        if (!gradual) RevealAll();
    }
    internal int Advance(double seconds)
    {
        if (!double.IsFinite(seconds) || seconds < 0) throw new ArgumentOutOfRangeException(nameof(seconds));
        int count = (int)Math.Min(elements.Length, Math.Floor(seconds * speed));
        VisibleLength = Math.Max(VisibleLength, count == elements.Length ? Text.Length : elements[count]);
        return VisibleLength;
    }
    internal void RevealAll() => VisibleLength = Text.Length;
}
