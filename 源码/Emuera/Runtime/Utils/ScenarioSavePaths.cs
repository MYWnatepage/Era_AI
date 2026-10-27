#nullable enable
using System;
using System.IO;
using System.Text.RegularExpressions;

namespace MinorShift.Emuera.Runtime.Utils;

// Only normal game saves are scoped. Shared display/API settings and global.sav stay shared.
internal static class ScenarioSavePaths
{
    internal static string Scope { get; private set; } = "";
    internal static Func<string, bool>? LegacyHeaderMatches { get; private set; }
    internal static void Select(string id, Func<string, bool>? legacyHeaderMatches = null)
    {
        if (!Regex.IsMatch(id, "^[a-zA-Z0-9_-]{1,64}$")) throw new ArgumentException("存档剧本编号无效。");
        Scope = id;
        LegacyHeaderMatches = legacyHeaderMatches;
    }
    internal static string Resolve(string directory, string filename, bool writing = false)
    {
        if (filename != Path.GetFileName(filename)) throw new ArgumentException("存档文件名无效。");
        string legacy = Path.Combine(directory, filename);
        if (Scope.Length == 0) return legacy;
        string scoped = Path.Combine(directory, "数据", "存档", Scope, filename);
        return writing || File.Exists(scoped) ? scoped : legacy;
    }
    // Emuera SAVEGAME prepends a timestamp before calling the script's SAVEINFO hook.
    private const string Timestamp = @"\d{4}[/.-]\d{2}[/.-]\d{2} \d{2}:\d{2}:\d{2} ";
    internal static string WithoutTimestamp(string text) => Regex.Replace(text, "^" + Timestamp, "", RegexOptions.CultureInvariant);
    internal static string DisplayCaption(string text) => Regex.Replace(text,
        "^(?<time>" + Timestamp + @")?\[scenario:[a-zA-Z0-9_-]{1,64}\] ?", "${time}", RegexOptions.CultureInvariant);
    internal static bool IsLegacy(string directory, string filename) =>
        Scope.Length > 0 && Path.GetFullPath(Path.GetDirectoryName(filename)!).Equals(Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase);
}
