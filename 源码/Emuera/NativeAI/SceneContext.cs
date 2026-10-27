#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MinorShift.Emuera.NativeAI;

// Current scene is save-bound data. Initial scenario prose is never the current location.
internal sealed class SceneState
{
    [JsonRequired] public string Location { get; set; } = "";
    [JsonRequired] public List<string> PresentCharacters { get; set; } = [];
    [JsonRequired] public string Summary { get; set; } = "";

    internal SceneState Copy() => new() { Location = Location, PresentCharacters = [.. PresentCharacters], Summary = Summary };
    internal void Validate(IReadOnlyDictionary<string, string> characters)
    {
        InventoryState.Text(Location, 120, true);
        InventoryState.Text(Summary, 600, false);
        if (PresentCharacters == null || PresentCharacters.Count > 256 ||
            PresentCharacters.Distinct(StringComparer.Ordinal).Count() != PresentCharacters.Count ||
            PresentCharacters.Any(c => c == null || !characters.ContainsKey(c)))
            throw new ArgumentException("场景包含未登记或重复的人物。");
    }
}

internal sealed class PlayerMapPosition
{
    public double X { get; set; }
    public double Z { get; set; }
    public string Location { get; set; } = "";
    public bool IsWilderness { get; set; }
}

// Initial location cards are content; copies in KnownScenes belong to this save.
internal sealed class SceneCard
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public List<int> Adjacent { get; set; } = [];
    public List<string> Facilities { get; set; } = [];
    public List<string> Aliases { get; set; } = [];
    public string Rules { get; set; } = "";
    public SceneMapPosition? Map { get; set; }
    internal SceneCard Copy() => new() { Id = Id, Name = Name, Description = Description, Adjacent = [.. Adjacent],
        Facilities = [.. Facilities], Aliases = [.. Aliases], Rules = Rules, Map = Map?.Copy() };
    internal void Validate()
    {
        Map?.Validate();
        if (Id < 1 || Id >= int.MaxValue || Adjacent == null || Adjacent.Count > 16 || Adjacent.Distinct().Count() != Adjacent.Count ||
            Adjacent.Contains(Id) || Facilities == null || Facilities.Count > 16 || Aliases == null || Aliases.Count > 12)
            throw new ArgumentException("场景卡编号或关联资料无效。");
        InventoryState.Text(Name, 120, true); InventoryState.Text(Description, 1200, false); InventoryState.Text(Rules, 600, false);
        foreach (string text in Facilities) InventoryState.Text(text, 100, true);
        foreach (string text in Aliases) InventoryState.Text(text, 80, true);
    }
}

// Optional normalized layout hints. Travel uses the resolved world geometry and its metre scale.
internal sealed class SceneMapPosition
{
    public double X { get; set; }
    public double Y { get; set; }
    public string Symbol { get; set; } = "place";
    internal SceneMapPosition Copy() => new() { X = X, Y = Y, Symbol = Symbol };
    internal void Validate()
    {
        if (!double.IsFinite(X) || !double.IsFinite(Y) || X is < 0 or > 1 || Y is < 0 or > 1 ||
            Symbol is not ("place" or "cabin" or "path" or "stream" or "forest" or "market"))
            throw new ArgumentException("场景地图位置或图形无效。");
    }
}

internal sealed class SceneCatalog
{
    public int FormatVersion { get; set; } = 1;
    public List<SceneCard> Scenes { get; set; } = [];
    public string MapFile { get; set; } = "";
    public List<SceneDescriptionMigration> DescriptionMigrations { get; set; } = [];
    [JsonIgnore] internal string SourceDirectory { get; set; } = "";
    internal void Validate()
    {
        if (FormatVersion != 1 || Scenes == null || Scenes.Count > 96) throw new ArgumentException("场景卡格式或数量无效。");
        foreach (var scene in Scenes) { if (scene == null) throw new ArgumentException("场景卡为空。"); scene.Validate(); }
        SceneDescriptionMigrations.Validate(this);
        if (Scenes.Select(s => s.Id).Distinct().Count() != Scenes.Count ||
            Scenes.Select(s => s.Name).Distinct(StringComparer.Ordinal).Count() != Scenes.Count ||
            Scenes.Any(s => s.Adjacent.Any(id => !Scenes.Any(other => other.Id == id))))
            throw new ArgumentException("场景卡编号、名称重复或连接指向未知场景。");
        if (Scenes.SelectMany(scene => scene.Aliases.Append(scene.Name).Distinct().Select(name => (name, scene.Id)))
            .GroupBy(entry => entry.name, StringComparer.Ordinal).Any(group => group.Select(entry => entry.Id).Distinct().Count() > 1))
            throw new ArgumentException("不同场景不能共用同一个名称或别名。");
    }
    internal static SceneCatalog Load(string scenarioDirectory, string relativeFile)
    {
        if (string.IsNullOrEmpty(relativeFile)) return new() { SourceDirectory = Path.GetFullPath(scenarioDirectory) };
        if (Path.IsPathRooted(relativeFile)) throw new ArgumentException("场景卡文件必须使用剧本内的相对路径。");
        string root = Path.GetFullPath(scenarioDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        string path = Path.GetFullPath(Path.Combine(root, relativeFile));
        if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase) || !File.Exists(path))
            throw new ArgumentException("场景卡文件不存在或超出本剧本目录。");
        // Junctions and symbolic links must not let a package read arbitrary external files.
        for (string? part = path; part != null && part.Length >= root.TrimEnd(Path.DirectorySeparatorChar).Length; part = Path.GetDirectoryName(part))
            if ((File.GetAttributes(part) & FileAttributes.ReparsePoint) != 0) throw new ArgumentException("场景卡路径不能经过链接目录或链接文件。");
        if (new FileInfo(path).Length > 256_000) throw new ArgumentException("场景卡文件过大。");
        string json = File.ReadAllText(path);
        using var doc = JsonDocument.Parse(json);
        InventoryReply.RejectDuplicateProperties(doc.RootElement);
        var catalog = JsonSerializer.Deserialize<SceneCatalog>(json, InventoryState.Json) ?? throw new JsonException();
        catalog.Validate();
        catalog.SourceDirectory = Path.GetFullPath(scenarioDirectory);
        return catalog;
    }
}

internal sealed partial class InventoryState
{
    public SceneState? Scene { get; set; }
    public PlayerMapPosition? PlayerPosition { get; set; }
    public decimal MovementRemainder { get; set; }
    public List<int> ContextItemIds { get; set; } = [];
    public Dictionary<string, string> CharacterLocations { get; set; } = new();
    public List<SceneCard> KnownScenes { get; set; } = [];
    public int NextSceneId { get; set; } = 1;
    public bool WorldInitialized { get; set; }
    public Dictionary<string, Dictionary<string, string>> SceneConditions { get; set; } = new();

    private void ValidateContext()
    {
        if (MovementRemainder is < 0 or >= 1) throw new ArgumentException("移动行动点余量无效。");
        if (PlayerPosition is { } position)
        {
            Text(position.Location, 120, true);
            if (!double.IsFinite(position.X) || !double.IsFinite(position.Z) || Math.Abs(position.X) > 1000000 || Math.Abs(position.Z) > 1000000 ||
                Scene == null || position.Location != Scene.Location) throw new ArgumentException("人物地图坐标与所在地不一致。");
        }
        Scene?.Validate(Characters);
        if (CharacterLocations == null || CharacterLocations.Count > 256 || KnownScenes == null || KnownScenes.Count > 192 || NextSceneId < 1 ||
            SceneConditions == null || SceneConditions.Count > 192 || SceneConditions.Values.Any(v => v == null) || SceneConditions.Values.Sum(v => v.Count) > 512)
            throw new ArgumentException("人物所在地或已知场景表无效。");
        foreach (var (character, location) in CharacterLocations)
        {
            if (!Characters.ContainsKey(character)) throw new ArgumentException("人物所在地包含未登记的人物。");
            Text(location, 120, true);
        }
        if (Scene != null && Scene.PresentCharacters.Any(c => CharacterLocations.TryGetValue(c, out string? location) && location != Scene.Location))
            throw new ArgumentException("在场人物与已保存所在地不一致。");
        foreach (var card in KnownScenes) { if (card == null) throw new ArgumentException("已知场景为空。"); card.Validate(); }
        foreach (var (location, conditions) in SceneConditions)
        {
            Text(location, 120, true);
            if (!KnownScenes.Any(card => card.Name == location) || conditions.Count > 16) throw new ArgumentException("场景设施状态包含未知地点或条目过多。");
            foreach (var (name, value) in conditions) { Text(name, 60, true); Text(value, 160, true); }
        }
        if (KnownScenes.Select(s => s.Id).Distinct().Count() != KnownScenes.Count ||
            KnownScenes.Select(s => s.Name).Distinct(StringComparer.Ordinal).Count() != KnownScenes.Count ||
            KnownScenes.Any(s => s.Id >= NextSceneId || s.Adjacent.Any(id => !KnownScenes.Any(other => other.Id == id))))
            throw new ArgumentException("已知场景编号或连接无效。");
        if (ContextItemIds == null || ContextItemIds.Count > 32 || ContextItemIds.Distinct().Count() != ContextItemIds.Count ||
            ContextItemIds.Any(id => !Items.Any(item => item.Id == id)))
            throw new ArgumentException("最近物品焦点无效。");
    }

    internal void SetScene(SceneState? scene)
    {
        if (scene == null) return;
        scene.Validate(Characters);
        scene = scene.Copy();
        scene.Location = CanonicalLocation(scene.Location);
        if (PlayerPosition != null && PlayerPosition.Location != scene.Location) PlayerPosition = null;
        if (Scene != null)
            foreach (string character in Scene.PresentCharacters) CharacterLocations.TryAdd(character, Scene.Location);
        // Leaving a character out does not move that character. Explicitly present people
        // move together; people left at the previous location keep that location.
        foreach (string character in scene.PresentCharacters) CharacterLocations[character] = scene.Location;
        RememberScene(scene.Location, scene.Summary);
        Scene = scene.Copy();
    }

    internal void BindWorld(ScenarioDefinition scenario)
    {
        // Import initial content once, including when migrating an older save. The save owns
        // its world copy; changing initial cards later cannot rewrite ongoing history.
        if (!WorldInitialized)
        {
            var map = new Dictionary<int, int>();
            var added = new List<(SceneCard card, SceneCard template)>();
            foreach (var template in scenario.World.Scenes)
            {
                var existing = KnownScenes.FirstOrDefault(card => card.Name == template.Name || template.Aliases.Contains(card.Name));
                if (existing != null)
                {
                    map[template.Id] = existing.Id;
                    // A just-created initial scene may precede the first content bind.
                    // Its card receives the static description once, not on future loads.
                    existing.Name = template.Name; existing.Description = template.Description;
                    existing.Facilities = [.. template.Facilities]; existing.Aliases = [.. template.Aliases]; existing.Rules = template.Rules;
                    added.Add((existing, template));
                    continue;
                }
                var copy = template.Copy();
                if (KnownScenes.Any(card => card.Id == copy.Id)) copy.Id = NextSceneId;
                NextSceneId = Math.Max(NextSceneId, checked(copy.Id + 1));
                KnownScenes.Add(copy); map[template.Id] = copy.Id; added.Add((copy, template));
            }
            foreach (var (card, template) in added) card.Adjacent = template.Adjacent.Select(id => map[id]).ToList();
            WorldInitialized = true;
        }
        if (Scene != null)
        {
            Scene.Location = CanonicalLocation(Scene.Location);
            foreach (string character in Scene.PresentCharacters) CharacterLocations.TryAdd(character, Scene.Location);
            RememberScene(Scene.Location, Scene.Summary);
        }
        foreach (string character in CharacterLocations.Keys.ToArray()) CharacterLocations[character] = CanonicalLocation(CharacterLocations[character]);
        foreach (string location in SceneConditions.Keys.ToArray())
        {
            string canonical = CanonicalLocation(location);
            if (canonical == location) continue;
            if (!SceneConditions.TryGetValue(canonical, out var existing)) SceneConditions[canonical] = existing = new();
            foreach (var entry in SceneConditions[location]) existing.TryAdd(entry.Key, entry.Value);
            SceneConditions.Remove(location);
        }
        // Unknown off-scene people remain unknown. Never replace a missing old scene
        // with InitialScene, or assume every character is at the opening location.
        SceneDescriptionMigrations.Apply(this, scenario.World);
        BindCharacterImportance(scenario);
        BindFatigue(scenario);
        ValidateContext();
    }

    internal void RememberScene(string location, string summary)
    {
        if (PlayerPosition is { IsWilderness: true } position && position.Location == location) return;
        if (KnownScenes.Any(s => s.Name == location)) return;
        if (KnownScenes.Count >= 192 || NextSceneId >= int.MaxValue) throw new ArgumentException("已知场景数量已达到上限。");
        KnownScenes.Add(new() { Id = NextSceneId++, Name = location, Description = summary });
    }

    internal string CanonicalLocation(string location) => KnownScenes.FirstOrDefault(card => card.Name == location || card.Aliases.Contains(location))?.Name ?? location;

    internal object WorldPrompt(string player)
    {
        var current = KnownScenes.FirstOrDefault(c => c.Name == Scene?.Location);
        var selected = new List<SceneCard>();
        if (current != null) selected.Add(current);
        selected.AddRange(KnownScenes.Where(c => player.Contains(c.Name, StringComparison.Ordinal) || c.Aliases.Any(a => player.Contains(a, StringComparison.Ordinal))));
        // Neighbours are listed as short names on the current card. Their full
        // descriptions are sent only when the player actually names a destination.
        var cards = selected.DistinctBy(c => c.Id).Take(4).ToList();
        var named = Characters.Where(pair => pair.Key == "player" || Scene?.PresentCharacters.Contains(pair.Key) == true ||
            player.Contains(pair.Value, StringComparison.Ordinal) || player.Contains(pair.Key, StringComparison.Ordinal)).Select(pair => pair.Key).ToHashSet();
        var locations = CharacterLocations.Where(p => named.Contains(p.Key)).OrderBy(p => p.Key == "player" ? 0 : 1).Take(12).ToDictionary(p => p.Key, p => p.Value);
        object Result() => new
        {
            player_position = PlayerPosition,
            scene_cards = cards.Select(c => new { id = c.Id, name = c.Name,
                description = Short(c.Description, c == current ? 400 : 300), adjacent = c.Adjacent.Take(8).Select(id => KnownScenes.FirstOrDefault(other => other.Id == id)?.Name).Where(n => n != null),
                facilities = c.Facilities.Take(2).Select(f => Short(f, 40)), rules = Short(c.Rules, 80),
                conditions = SceneConditions.GetValueOrDefault(c.Name) ?? new() }),
            character_locations = locations,
            note = "只提供当前和点名地点的详情，相邻地点仅列名称。人物所在地是已确认记录，未列出的位置未知；场外人物不会自动同行。"
        };
        // Keep the current card first and drop lower-priority mentioned descriptions
        // before letting scene reference data crowd out the player's action or story.
        while (cards.Count > 1 && JsonSerializer.Serialize(Result(), Json).Length > 3000) cards.RemoveAt(cards.Count - 1);
        return Result();
    }
    private static string Short(string value, int length) => value.Length <= length ? value : value[..length];

    internal SceneState? ResolveActionScene(SceneState? target, IEnumerable<string>? participants = null)
    {
        if (target == null) return null;
        target.Validate(Characters);
        var moving = (participants ?? (Characters.ContainsKey("player") ? ["player"] : Array.Empty<string>())).Distinct().ToList();
        if (moving.Any(c => !Characters.ContainsKey(c))) throw new ArgumentException("同行人物未登记。");
        string destination = CanonicalLocation(target.Location);
        var present = CharacterLocations.Where(pair => pair.Value == destination && !moving.Contains(pair.Key)).Select(pair => pair.Key)
            .Concat(moving).Distinct().ToList();
        return new() { Location = destination, PresentCharacters = present,
            Summary = SceneNarration.Arrival(destination) };
    }

    internal SceneState LocalScene(ScenarioDefinition scenario, string key, IEnumerable<string>? participants = null)
    {
        if (!scenario.LocalScenes.TryGetValue(key, out var target)) throw new ArgumentException("剧本未定义此本地行动的结束地点。");
        var resolved = ResolveActionScene(target, participants)!;
        SetScene(resolved);
        return resolved;
    }

    internal string WorldDisplay()
    {
        var lines = new List<string> { "已知场景", "当前地点：" + (Scene?.Location ?? "未知（旧档尚未确认）") };
        foreach (var card in KnownScenes.OrderBy(c => c.Id))
        {
            lines.Add($"场景 {card.Id} · {card.Name}：{card.Description}");
            if (card.Adjacent.Count > 0) lines.Add("    相邻：" + string.Join("、", card.Adjacent.Select(id => KnownScenes.First(c => c.Id == id).Name)));
            if (card.Facilities.Count > 0) lines.Add("    设施：" + string.Join("、", card.Facilities));
            if (card.Rules.Length > 0) lines.Add("    规则：" + card.Rules);
            if (SceneConditions.TryGetValue(card.Name, out var conditions))
                foreach (var (name, value) in conditions) lines.Add("    当前状态：" + name + " / " + value);
        }
        lines.Add("人物所在地");
        foreach (var character in Characters) lines.Add(character.Value + "：" + CharacterLocations.GetValueOrDefault(character.Key, "未知"));
        return string.Join("\n", lines);
    }

    internal string RestNarrative()
    {
        if (Scene == null) return "你放下手边的事，准备歇息。";
        var companions = Scene.PresentCharacters.Where(c => c != "player").Select(c => Characters[c]).ToList();
        return companions.Count == 0 ? "你在" + Scene.Location + "安顿下来，准备歇息。" :
            "你在" + Scene.Location + "与" + string.Join("、", companions) + "安顿下来，准备歇息。";
    }
}

internal static partial class InventoryReply
{
    private const string PlayerMovementRequired = "主角移动必须通过行动菜单的“移动”进入地图，绘制并确认路线后按距离结算；本轮场景变化未采用。";

    private static bool ApplyScene(InventoryExchange context, JsonElement root, InventoryState state,
        List<string> changes, List<string> rationale)
    {
        if (!root.TryGetProperty("scene_update", out var update)) return false;
        if (context.Action != null) throw new ArgumentException("程序行动的结束场景由程序设置，模型不得追加场景变化。");
        var fields = new List<string> { "location", "present_characters", "summary", "reason" };
        if (update.TryGetProperty("character_locations", out _)) fields.Add("character_locations");
        if (update.TryGetProperty("conditions", out _)) fields.Add("conditions");
        Fields(update, fields.ToArray());
        var present = update.GetProperty("present_characters");
        if (present.ValueKind != JsonValueKind.Array || present.GetArrayLength() > 256)
            throw new ArgumentException("在场人物列表无效。");
        var scene = new SceneState
        {
            Location = String(update, "location", 120),
            Summary = String(update, "summary", 600, false),
            PresentCharacters = present.EnumerateArray().Select(v =>
            {
                if (v.ValueKind != JsonValueKind.String) throw new ArgumentException("在场人物编号必须是文字。");
                return v.GetString()!;
            }).ToList(),
        };
        scene.Validate(state.Characters);
        scene.Location = state.CanonicalLocation(scene.Location);
        string reason = String(update, "reason", 300);
        var movements = new Dictionary<string, string>();
        var conditionChanges = new Dictionary<string, string>();
        if (update.TryGetProperty("conditions", out var conditions))
        {
            if (conditions.ValueKind != JsonValueKind.Object || conditions.EnumerateObject().Count() > 16)
                throw new ArgumentException("场景设施状态需要最多16项的文字键值表。");
            foreach (var pair in conditions.EnumerateObject())
            {
                InventoryState.Text(pair.Name, 60, true);
                if (pair.Value.ValueKind != JsonValueKind.String) throw new ArgumentException("场景设施状态的值必须是文字。");
                string value = pair.Value.GetString()!;
                InventoryState.Text(value, 160, true);
                conditionChanges.Add(pair.Name, value);
            }
            if ((state.SceneConditions.GetValueOrDefault(scene.Location)?.Keys.Union(conditionChanges.Keys).Count() ?? conditionChanges.Count) > 16)
                throw new ArgumentException("当前地点的设施状态条目超出上限。");
        }
        if (update.TryGetProperty("character_locations", out var locations))
        {
            if (locations.ValueKind != JsonValueKind.Object || locations.EnumerateObject().Count() > 256)
                throw new ArgumentException("人物所在地更新需要人物编号到地点文字的对象。");
            foreach (var pair in locations.EnumerateObject())
            {
                if (!state.Characters.ContainsKey(pair.Name) || pair.Value.ValueKind != JsonValueKind.String)
                    throw new ArgumentException("人物所在地更新包含未登记人物或非文字地点。");
                string destination = state.CanonicalLocation(pair.Value.GetString()!);
                InventoryState.Text(destination, 120, true);
                if (pair.Name == "player" && destination != scene.Location)
                    throw new ArgumentException(PlayerMovementRequired);
                if (scene.PresentCharacters.Contains(pair.Name) && destination != scene.Location)
                    throw new ArgumentException("在场人物的所在地必须与当前场景一致。");
                if (!scene.PresentCharacters.Contains(pair.Name) && destination == scene.Location)
                    throw new ArgumentException("位于当前地点的人物也必须列入在场人物。");
                movements.Add(pair.Name, destination);
            }
        }
        ValidatePlayerSceneUpdate(state, scene, movements);
        var finalLocations = new Dictionary<string, string>(state.CharacterLocations);
        if (state.Scene != null)
            foreach (string character in state.Scene.PresentCharacters) finalLocations.TryAdd(character, state.Scene.Location);
        foreach (string character in scene.PresentCharacters) finalLocations[character] = scene.Location;
        foreach (var pair in movements) finalLocations[pair.Key] = pair.Value;
        if (finalLocations.Any(pair => pair.Value == scene.Location && !scene.PresentCharacters.Contains(pair.Key)))
            throw new ArgumentException("仍记录在当前地点的人物必须列入在场人物；如已离开，请提供其明确去向。");
        bool changed = state.Scene == null || state.Scene.Location != scene.Location ||
            !state.Scene.PresentCharacters.OrderBy(c => c).SequenceEqual(scene.PresentCharacters.OrderBy(c => c)) ||
            movements.Any(pair => state.CharacterLocations.GetValueOrDefault(pair.Key) != pair.Value) ||
            conditionChanges.Any(pair => state.SceneConditions.GetValueOrDefault(scene.Location)?.GetValueOrDefault(pair.Key) != pair.Value);
        state.SetScene(scene);
        foreach (var (character, location) in movements)
        {
            if (state.CharacterLocations.GetValueOrDefault(character) != location)
                changes.Add(state.Characters[character] + " 所在地：" + location);
            state.CharacterLocations[character] = location;
            state.RememberScene(location, "这里的环境还不熟悉。");
        }
        if (conditionChanges.Count > 0)
        {
            if (!state.SceneConditions.TryGetValue(scene.Location, out var currentConditions)) state.SceneConditions[scene.Location] = currentConditions = new();
            foreach (var (name, value) in conditionChanges)
            {
                if (currentConditions.GetValueOrDefault(name) != value) changes.Add(scene.Location + "：" + name + " → " + value);
                currentConditions[name] = value;
            }
        }
        if (changed)
        {
            changes.Add("当前地点：" + scene.Location + " / 在场人物：" +
                string.Join("、", scene.PresentCharacters.Select(c => state.Characters[c])));
            rationale.Add("场景变化依据：" + reason);
        }
        return changed;
    }

    private static void ValidatePlayerSceneUpdate(InventoryState state, SceneState scene,
        IReadOnlyDictionary<string, string> movements)
    {
        if (!state.Characters.ContainsKey("player")) return;
        // AI scene updates describe the player's existing viewpoint. Only a locally
        // measured map route may move that viewpoint or invalidate its precise point.
        // Old saves may confirm an already recorded player location, never guess one.
        string? location = state.Scene?.Location ?? state.CharacterLocations.GetValueOrDefault("player");
        if (string.IsNullOrEmpty(location))
            throw new ArgumentException("当前存档的主角所在地尚未确认，不能由剧情猜测；请先确认当前位置，再通过移动地图规划路线。");
        location = state.CanonicalLocation(location);
        if (scene.Location != location ||
            movements.TryGetValue("player", out string? destination) && destination != location)
            throw new ArgumentException(PlayerMovementRequired);
        if (!scene.PresentCharacters.Contains("player"))
            throw new ArgumentException("当前场景必须保留主角在场；请通过行动菜单的“移动”在地图上规划路线，不能用剧情把主角移到场外。");
    }

    private static void CaptureItemFocus(InventoryState state, JsonElement ops, int firstNewId, ScenarioActionOutcome? action)
    {
        var ids = new List<int>();
        foreach (var op in ops.EnumerateArray())
        {
            if (op.TryGetProperty("item_id", out var id) && id.TryGetInt32(out int itemId)) ids.Add(itemId);
        }
        ids.AddRange(state.Items.Where(i => i.Id >= firstNewId).Select(i => i.Id));
        if (action != null) ids.Add(action.ItemId);
        // Keep the last committed change across ordinary chats for “the one just found”.
        if (ids.Count > 0) state.ContextItemIds = ids.Distinct().TakeLast(32).ToList();
    }
}
