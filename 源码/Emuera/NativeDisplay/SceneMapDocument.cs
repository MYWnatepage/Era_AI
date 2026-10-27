#nullable enable
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text.Json;
using MinorShift.Emuera.NativeAI;

namespace MinorShift.Emuera.NativeDisplay;

// The map receives a detached presentation snapshot. It cannot write gameplay state.
internal static class SceneMapDocument
{
    private const int MaximumLayoutBytes = 256_000;
    private static readonly JsonSerializerOptions DocumentJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        // Keep the default HTML-safe encoder: save text is inserted inside a script.
    };

    internal static string BuildPayload(InventoryState state, SceneCatalog? initial, DisplayTheme theme, ScenarioDefinition? scenario = null)
    {
        ArgumentNullException.ThrowIfNull(state);
        var world = InventoryState.Read(state.Snapshot());
        SceneDescriptionMigrations.Apply(world, initial);
        var safe = theme.Safe();
        var currentLocation = world.Scene?.Location ?? world.CharacterLocations.GetValueOrDefault("player") ?? "";
        var current = world.KnownScenes.FirstOrDefault(card => SameLocation(card, currentLocation));
        var sites = world.KnownScenes.OrderBy(card => card.Id).Select(card =>
        {
            // Save identifiers may have shifted after legacy migration. Match authored
            // layout identifiers by names/aliases, never by coincidentally equal IDs.
            var matches = initial?.Scenes.Where(candidate => candidate.Name == card.Name || candidate.Aliases.Contains(card.Name)).ToArray();
            var template = matches?.Length == 1 ? matches[0] : null;
            var map = card.Map?.Copy() ?? template?.Map?.Copy();
            return new
            {
                id = card.Id,
                layoutId = template?.Id ?? 0,
                name = card.Name,
                description = card.Description,
                actions = SceneActionPresentation.ForScene(world, scenario, card),
                benefits = SceneActionPresentation.BenefitsForScene(world, scenario, card),
                facilities = card.Facilities.ToArray(),
                adjacent = card.Adjacent.Select(id => world.KnownScenes.FirstOrDefault(other => other.Id == id)?.Name)
                    .Where(name => name != null).ToArray(),
                people = world.Characters.Where(character =>
                    world.CharacterLocations.TryGetValue(character.Key, out string? location)
                        ? SameLocation(card, location)
                        : world.Scene != null && SameLocation(card, world.Scene.Location) && world.Scene.PresentCharacters.Contains(character.Key))
                    .Select(character => character.Value).ToArray(),
                conditions = world.SceneConditions.TryGetValue(card.Name, out var conditions)
                    ? conditions.Select(condition => condition.Key + "：" + condition.Value).ToArray() : Array.Empty<string>(),
                map = map == null ? null : new { x = map.X, y = map.Y, symbol = map.Symbol }
            };
        }).ToArray();
        return JsonSerializer.Serialize(new
        {
            sites,
            currentId = current?.Id ?? 0,
            currentLocation,
            playerPosition = world.PlayerPosition,
            layout = LoadLayout(initial),
            palette = new { background = Hex(safe.BackgroundColor), foreground = Hex(safe.TextColor), focus = Hex(ColorSafety.Selection(safe)) },
            fontFamily = "Microsoft YaHei UI"
        }, DocumentJson);
    }

#if ERA_WEB_MAP_REFERENCE
    internal static string Create(InventoryState state, SceneCatalog? initial, DisplayTheme theme)
    {
        string template = ReadResource("EraAI.SceneMap.Document.html");
        string worldScript = ReadResource("EraAI.SceneMap.World.js");
        if (!template.Contains("/*__INPUT__*/", StringComparison.Ordinal) || !template.Contains("/*__WORLD__*/", StringComparison.Ordinal))
            throw new IOException("内置地图页面缺少数据入口。");
        // Insert save text last, so marker-looking text in a description cannot
        // accidentally be treated as a second template substitution.
        return template.Replace("/*__WORLD__*/", worldScript, StringComparison.Ordinal)
            .Replace("/*__INPUT__*/", "window.EraSceneMapInput=" + BuildPayload(state, initial, theme) + ";", StringComparison.Ordinal);
    }

#endif
    private static bool SameLocation(SceneCard card, string location) =>
        location.Length > 0 && (card.Name == location || card.Aliases.Contains(location));

    private static string Hex(Color color) => $"#{color.R:x2}{color.G:x2}{color.B:x2}";

#if ERA_WEB_MAP_REFERENCE
    private static string ReadResource(string name)
    {
        using var stream = typeof(SceneMapDocument).Assembly.GetManifestResourceStream(name)
            ?? throw new IOException("内置地图资源缺失。");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

#endif
    private static JsonElement? LoadLayout(SceneCatalog? catalog)
    {
        if (catalog == null || string.IsNullOrWhiteSpace(catalog.MapFile)) return null;
        string relative = catalog.MapFile;
        if (relative.Length > 260 || Path.IsPathRooted(relative) || relative.Contains(':') || relative.IndexOf('\0') >= 0 ||
            string.IsNullOrWhiteSpace(catalog.SourceDirectory))
            throw new ArgumentException("地图布局文件必须使用剧本内的相对路径。");
        string directory = Path.GetFullPath(catalog.SourceDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string root = directory + Path.DirectorySeparatorChar;
        string path = Path.GetFullPath(Path.Combine(root, relative));
        if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase) || !File.Exists(path))
            throw new ArgumentException("地图布局文件不存在或超出本剧本目录。");
        for (string? part = path; part != null && part.Length >= directory.Length; part = Path.GetDirectoryName(part))
            if ((File.GetAttributes(part) & FileAttributes.ReparsePoint) != 0)
                throw new ArgumentException("地图布局路径不能经过链接目录或链接文件。");
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > MaximumLayoutBytes) throw new ArgumentException("地图布局文件过大。");
        try
        {
            using var document = JsonDocument.Parse(stream, new JsonDocumentOptions { MaxDepth = 48 });
            var layout = document.RootElement;
            InventoryReply.RejectDuplicateProperties(layout);
            ValidateLayout(layout);
            return layout.Clone();
        }
        catch (JsonException error) { throw new ArgumentException("地图布局文件格式无效。", error); }
    }

    private static void ValidateLayout(JsonElement layout)
    {
        if (layout.ValueKind != JsonValueKind.Object || !layout.TryGetProperty("format_version", out var version) ||
            version.ValueKind != JsonValueKind.Number || !version.TryGetInt32(out int format) || format != 1 || !layout.TryGetProperty("sites", out var sites) ||
            sites.ValueKind != JsonValueKind.Array || sites.GetArrayLength() > 192)
            throw new ArgumentException("地图布局版本或地点列表无效。");
        var identifiers = new HashSet<int>();
        foreach (var site in sites.EnumerateArray())
        {
            if (site.ValueKind != JsonValueKind.Object || !site.TryGetProperty("id", out var id) ||
                id.ValueKind != JsonValueKind.Number || !id.TryGetInt32(out int identifier) || identifier < 1 || !identifiers.Add(identifier) ||
                !site.TryGetProperty("x", out var x) || !ValidCoordinate(x) ||
                !site.TryGetProperty("z", out var z) || !ValidCoordinate(z))
                throw new ArgumentException("地图布局地点编号或坐标无效。");
        }
        if (layout.TryGetProperty("meters_per_unit", out var scale) &&
            (scale.ValueKind != JsonValueKind.Number || !scale.TryGetDouble(out double value) || !double.IsFinite(value) || value <= 0 || value > 100_000))
            throw new ArgumentException("地图比例尺无效。");
        if (layout.TryGetProperty("origin_scene", out var origin) &&
            (origin.ValueKind != JsonValueKind.Number || !origin.TryGetInt32(out int originId) || !identifiers.Contains(originId)))
            throw new ArgumentException("地图原点未对应已定义的布局地点。");
        ValidateFinite(layout);
    }

    private static bool ValidCoordinate(JsonElement value) => value.ValueKind == JsonValueKind.Number &&
        value.TryGetDouble(out double number) && double.IsFinite(number) && Math.Abs(number) <= 100_000;

    private static void ValidateFinite(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Number &&
            (!value.TryGetDouble(out double number) || !double.IsFinite(number) || !float.IsFinite((float)number)))
            throw new ArgumentException("地图布局包含无效数值。");
        if (value.ValueKind == JsonValueKind.Object)
            foreach (var property in value.EnumerateObject()) ValidateFinite(property.Value);
        else if (value.ValueKind == JsonValueKind.Array)
            foreach (var item in value.EnumerateArray()) ValidateFinite(item);
    }
}
