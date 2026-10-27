#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Text.Json.Serialization;

namespace MinorShift.Emuera.NativeAI;

// Content belongs to ERB/script/<package>/剧本.json, never to the executable.
internal sealed class ScenarioDefinition
{
    public int FormatVersion { get; set; } = 1;
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Author { get; set; } = "";
    public string Type { get; set; } = "";
    public string Version { get; set; } = "";
    public string Entry { get; set; } = "";
    public string Resume { get; set; } = "";
    public string Init { get; set; } = "";
    public string BeforeSave { get; set; } = "";
    public string AfterLoad { get; set; } = "";
    public string TurnEnd { get; set; } = "";
    public string SaveCaption { get; set; } = "";
    public string LegacyProbe { get; set; } = "";
    public string[] LegacySaveLabels { get; set; } = [];
    public string ReplyLabel { get; set; } = "回应";
    public Dictionary<string, string> Owners { get; set; } = new() { ["player"] = "主角", ["world"] = "无主" };
    public Dictionary<string, string> Characters { get; set; } = new();
    public string[] MajorCharacters { get; set; } = [];
    public Dictionary<string, string> Content { get; set; } = new();
    internal string ContentValue(string key) => Content.TryGetValue(key, out var value) ? value : throw new ArgumentException("剧本未填写此资料：" + key);
    public Dictionary<string, int> Parameters { get; set; } = new();
    public PromptSelectionRules ContextSelection { get; set; } = new();
    public SceneState? InitialScene { get; set; }
    public Dictionary<string, SceneState> LocalScenes { get; set; } = new();
    public string WorldFile { get; set; } = "";
    public string ItemTextMigrationsFile { get; set; } = "";
    [JsonIgnore] public InventoryTextMigrations ItemTextMigrations { get; set; } = new();
    [JsonIgnore] public SceneCatalog World { get; set; } = new();
    public string[] DefaultSuggestions { get; set; } = [];
    public string InventoryInstruction { get; set; } = "";
    public string[] CommandPrefixes { get; set; } = [""];
    public string[] CommandSuffixes { get; set; } = [""];
    public Dictionary<string, string[]> Commands { get; set; } = new();
    public Dictionary<string, ScenarioAction> Actions { get; set; } = new();
    // Optional per-action scene IDs belong to the scenario, not to engine code or model prose.
    public Dictionary<string, List<int>> ActionLocations { get; set; } = new();
    public Dictionary<string, ScenarioLocationAction> LocationActions { get; set; } = new();
    public Dictionary<string, ScenarioRecoveryRule> RecoveryRules { get; set; } = new();
    public FatigueRules FatigueRules { get; set; } = new();

    internal string SavePrefix => "[scenario:" + Id + "]";
    internal string[] Metadata() => [Id, Name, Entry, Resume, Init, BeforeSave, AfterLoad, TurnEnd, SaveCaption, LegacyProbe];
    internal ScenarioActionOutcome Resolve(string key, int roll)
    {
        if (!Actions.TryGetValue(key, out var action)) throw new ArgumentException("剧本未定义此行动。");
        return action.Resolve(roll);
    }
    internal int Parameter(string key) => Parameters.TryGetValue(key, out int value) ? value : throw new ArgumentException("剧本未定义此规则参数。");
    internal bool MatchesSave(string text)
    {
        text = MinorShift.Emuera.Runtime.Utils.ScenarioSavePaths.WithoutTimestamp(text);
        // New headers always have an exact machine ID; legacy aliases are fallback only.
        if (text.StartsWith("[scenario:", StringComparison.Ordinal)) return text.StartsWith(SavePrefix + " ", StringComparison.Ordinal) || text == SavePrefix;
        return LegacySaveLabels.Any(label => text == label || text.StartsWith(label + " ", StringComparison.Ordinal) ||
            text == "自动存档：" + label || text.StartsWith("自动存档：" + label + " ", StringComparison.Ordinal));
    }
    internal void Validate()
    {
        if (FormatVersion != 1 || !Regex.IsMatch(Id, "^[a-zA-Z0-9_-]{1,64}$")) throw new ArgumentException("剧本编号或登记格式版本无效。");
        InventoryState.Text(Name, 100, true); InventoryState.Text(Author, 100, false); InventoryState.Text(Type, 40, false);
        InventoryState.Text(Version, 80, false); InventoryState.Text(ReplyLabel, 80, true);
        foreach (string label in new[] { Name, Author, Type, ReplyLabel })
            if (label.Any(char.IsControl)) throw new ArgumentException("剧本登记名称不能换行。");
        foreach (string hook in new[] { Entry, Resume, Init, BeforeSave, AfterLoad, TurnEnd, SaveCaption, LegacyProbe })
            if (hook == null || (hook.Length > 0 && !Regex.IsMatch(hook, "^[A-Za-z_][A-Za-z0-9_]{0,127}$"))) throw new ArgumentException("剧本入口必须是有效的 ERB 函数名。");
        if (Entry.Length == 0 || Resume.Length == 0) throw new ArgumentException("剧本缺少启动或继续入口。");
        if (Owners == null || Owners.Count is < 1 or > 256) throw new ArgumentException("剧本归属登记无效。");
        foreach (var (key, value) in Owners) { InventoryState.Owner(key); InventoryState.Text(value, 80, true); }
        if (Characters == null || Characters.Count > 256 || Content == null || Content.Count > 128)
            throw new ArgumentException("剧本人物登记或文字资料无效。");
        foreach (var pair in Characters) { InventoryState.Owner(pair.Key); InventoryState.Text(pair.Value, 80, true); }
        if (MajorCharacters == null || MajorCharacters.Length > 256 ||
            MajorCharacters.Distinct(StringComparer.Ordinal).Count() != MajorCharacters.Length ||
            MajorCharacters.Any(character => character == null || !Characters.ContainsKey(character)))
            throw new ArgumentException("主要人物登记包含未知或重复人物。");
        if (ContextSelection == null || LocalScenes == null || LocalScenes.Count > 32) throw new ArgumentException("剧本上下文配置无效。");
        ContextSelection.Validate();
        InitialScene?.Validate(Characters);
        foreach (var (key, scene) in LocalScenes) { InventoryState.Owner(key); if (scene == null) throw new ArgumentException("本地场景为空。"); scene.Validate(Characters); }
        World.Validate();
        ItemTextMigrations.Validate();
        if (ActionLocations == null || ActionLocations.Count > 16 || ActionLocations.Any(pair =>
            pair.Key is not ("hunt" or "sleep" or "rest" or "move" or "trade") || pair.Value == null || pair.Value.Count > 96 ||
            pair.Value.Distinct().Count() != pair.Value.Count || pair.Value.Any(id => !World.Scenes.Any(scene => scene.Id == id))))
            throw new ArgumentException("行动地点规则包含未知行动、重复地点或未登记场景。");
        if (LocationActions == null || LocationActions.Count > 2 || LocationActions.Keys.Any(key => key is not ("hunt" or "trade")))
            throw new ArgumentException("地点专属行动登记无效。");
        foreach (var action in LocationActions.Values) { if (action == null) throw new ArgumentException("地点行动资料为空。"); action.Validate(); }
        if (RecoveryRules == null || RecoveryRules.Count > 2 || RecoveryRules.Keys.Any(key => key is not ("rest" or "sleep")))
            throw new ArgumentException("休息恢复规则无效。");
        foreach (var rule in RecoveryRules.Values) { if (rule == null) throw new ArgumentException("恢复规则为空。"); rule.Validate(World); }
        if (FatigueRules == null) throw new ArgumentException("疲劳点规则为空。");
        FatigueRules.Validate(World);
        if (FatigueRules.Enabled && !Characters.ContainsKey("player")) throw new ArgumentException("疲劳点需要登记主角。");

        foreach (var pair in Content) { InventoryState.Owner(pair.Key); InventoryState.Text(pair.Value, 20000, false); }
        if (Parameters == null || Parameters.Count > 128 || Parameters.Keys.Any(k => !Regex.IsMatch(k, "^[A-Za-z0-9_-]{1,64}$"))) throw new ArgumentException("剧本规则参数无效。");
        if (Parameters.TryGetValue("meters_per_action_point", out int metres) && metres is < 1 or > 1000000)
            throw new ArgumentException("每行动点移动距离必须为1至1000000米。");
        if (DefaultSuggestions == null || DefaultSuggestions.Length > 3) throw new ArgumentException("默认建议最多三条。");
        foreach (string text in DefaultSuggestions) InventoryState.Text(text, 80, true);
        if (LegacySaveLabels == null || LegacySaveLabels.Length > 20) throw new ArgumentException("旧存档别名无效。");
        foreach (string alias in LegacySaveLabels) InventoryState.Text(alias, 100, true);
        InventoryState.Text(InventoryInstruction, 6000, false);
        if (Commands == null || CommandPrefixes == null || CommandSuffixes == null || Commands.Count > 32 || CommandPrefixes.Length > 64 || CommandSuffixes.Length > 16)
            throw new ArgumentException("剧本行动指令登记无效。");
        var exactCommands = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (key, words) in Commands)
        {
            if (!int.TryParse(key, out int code) || code < 1 || words == null || words.Length is < 1 or > 32) throw new ArgumentException("行动编号或词语无效。");
            foreach (string word in words)
            {
                InventoryState.Text(word, 40, true);
                foreach (string prefix in CommandPrefixes)
                    foreach (string suffix in CommandSuffixes)
                    {
                        InventoryState.Text(prefix, 40, false); InventoryState.Text(suffix, 20, false);
                        if (!exactCommands.Add(prefix + word + suffix)) throw new ArgumentException("行动指令存在重复或歧义。");
                    }
            }
        }
        if (Actions == null || Actions.Count > 32) throw new ArgumentException("程序行动登记无效。");
        foreach (var action in Actions.Values) { action.Validate(Owners); action.EndScene?.Validate(Characters); }
    }
}

internal sealed class ScenarioLocationAction
{
    public string Label { get; set; } = "";
    public string Hint { get; set; } = "";
    public string Input { get; set; } = "";
    internal void Validate()
    {
        InventoryState.Text(Label, 40, true); InventoryState.Text(Hint, 200, false); InventoryState.Text(Input, 1000, false);
        if (Label.Any(char.IsControl) || Hint.Any(char.IsControl)) throw new ArgumentException("行动名称与提示必须为一行文字。");
    }
}

internal sealed class ScenarioRecoveryRule
{
    public int Levels { get; set; }
    public string Label { get; set; } = "疲劳恢复";
    // Optional legacy restriction. Empty means the stable category controls recovery;
    // narrative names such as 搬运疲劳 are not a machine-readable recovery type.
    public string[] StatusNames { get; set; } = [];
    public string[] Categories { get; set; } = [];
    public Dictionary<string, int> SeverityLevels { get; set; } = new();
    public string[] SeverityLabels { get; set; } = [];
    public Dictionary<int, int> LocationBonus { get; set; } = new();
    internal void Validate(SceneCatalog world)
    {
        if (Levels is < 0 or > 8 || StatusNames == null || StatusNames.Length > 16 ||
            Categories == null || Categories.Length is < 1 or > 8 || SeverityLevels == null || SeverityLevels.Count is < 1 or > 32 ||
            SeverityLabels == null || SeverityLabels.Length is < 1 or > 8 || LocationBonus == null || LocationBonus.Count > 96 ||
            SeverityLevels.Values.Any(level => level < 1 || level > SeverityLabels.Length) ||
            LocationBonus.Any(pair => pair.Value is < 0 or > 8 || !world.Scenes.Any(scene => scene.Id == pair.Key)))
            throw new ArgumentException("休息恢复等级或地点加成无效。");
        InventoryState.Text(Label, 80, true);
        foreach (string text in StatusNames) InventoryState.Text(text, 80, true);
        foreach (string text in Categories.Concat(SeverityLevels.Keys).Concat(SeverityLabels)) InventoryState.Text(text, 40, true);
    }
}

internal sealed class ScenarioAction
{
    public int Sides { get; set; }
    public int ItemId { get; set; }
    public string Owner { get; set; } = "";
    public string Summary { get; set; } = "";
    public string Story { get; set; } = "";
    public string Instruction { get; set; } = "";
    public string[] QuantityPatterns { get; set; } = [];
    public SceneState? EndScene { get; set; }
    public List<ScenarioOutcomeBand> Outcomes { get; set; } = [];
    internal void Validate(Dictionary<string, string> owners)
    {
        if (Sides is < 2 or > 10000 || ItemId < 1 || !owners.ContainsKey(Owner) || Outcomes == null || Outcomes.Count is < 1 or > 100)
            throw new ArgumentException("行动骰子、物品或归属配置无效。");
        InventoryState.Text(Summary, 1000, true); InventoryState.Text(Story, 4000, true); InventoryState.Text(Instruction, 6000, true);
        int next = 1;
        foreach (var band in Outcomes.OrderBy(b => b.Min))
        {
            if (band.Min != next || band.Max < band.Min || band.Max > Sides || band.Quantity < 0 || band.Quantity > InventoryState.MaxQuantity)
                throw new ArgumentException("行动结果区间必须完整覆盖骰点，且不能重叠。");
            InventoryState.Text(band.Tier, 80, true); InventoryState.Text(band.Event, 2000, true);
            next = band.Max + 1;
        }
        if (next != Sides + 1 || QuantityPatterns == null || QuantityPatterns.Length > 8) throw new ArgumentException("行动结果或数量校验规则不完整。");
        foreach (string pattern in QuantityPatterns)
        {
            InventoryState.Text(pattern, 2000, true);
            var regex = new Regex(pattern, RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
            if (!regex.GetGroupNames().Contains("quantity")) throw new ArgumentException("数量校验规则缺少 quantity 捕获组。");
        }
    }
    internal ScenarioActionOutcome Resolve(int roll)
    {
        if (roll < 1 || roll > Sides) throw new ArgumentException("骰点超出剧本配置范围。");
        var band = Outcomes.Single(b => roll >= b.Min && roll <= b.Max);
        string Format(string text) => text.Replace("{roll}", roll.ToString()).Replace("{quantity}", band.Quantity.ToString())
            .Replace("{tier}", band.Tier).Replace("{event}", band.Event).Replace("{sides}", Sides.ToString());
        return new(roll, band.Quantity, ItemId, Owner, Format(Summary), Format(Story), Format(Instruction), QuantityPatterns.ToArray(), Sides, EndScene?.Copy());
    }
}
internal sealed class ScenarioOutcomeBand
{
    public int Min { get; set; }
    public int Max { get; set; }
    public int Quantity { get; set; }
    public string Tier { get; set; } = "";
    public string Event { get; set; } = "";
}
internal sealed record ScenarioActionOutcome(int Roll, int Quantity, int ItemId, string Owner, string Summary, string Story, string Instruction, string[] QuantityPatterns, int Sides = 0, SceneState? EndScene = null,
    int RawRoll = 0, int FatiguePenalty = 0, decimal FatigueCost = 0);

internal sealed class ScenarioCatalog
{
    internal List<ScenarioDefinition> Entries { get; } = [];
    internal List<string> Errors { get; } = [];
    internal static ScenarioCatalog Load(string root)
    {
        var catalog = new ScenarioCatalog();
        if (!Directory.Exists(root)) return catalog;
        var candidates = new List<ScenarioDefinition>();
        foreach (string file in Directory.EnumerateFiles(root, "剧本.json", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint }).OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                if (new FileInfo(file).Length > 256_000) throw new ArgumentException("登记文件过大。");
                string json = File.ReadAllText(file);
                using var doc = JsonDocument.Parse(json);
                InventoryReply.RejectDuplicateProperties(doc.RootElement);
                var entry = JsonSerializer.Deserialize<ScenarioDefinition>(json, InventoryState.Json) ?? throw new JsonException();
                entry.World = SceneCatalog.Load(Path.GetDirectoryName(file)!, entry.WorldFile);
                entry.ItemTextMigrations = InventoryTextMigrations.Load(Path.GetDirectoryName(file)!, entry.ItemTextMigrationsFile);
                entry.Validate();
                // A missing ERB file must be reported before entering the script, not as a runtime crash.
                var labels = new HashSet<string>(Directory.EnumerateFiles(Path.GetDirectoryName(file)!, "*", SearchOption.AllDirectories)
                    .Where(f => Path.GetExtension(f).Equals(".erb", StringComparison.OrdinalIgnoreCase))
                    .SelectMany(f => Regex.Matches(File.ReadAllText(f), @"(?m)^\s*@([A-Za-z_][A-Za-z0-9_]*)").Select(m => m.Groups[1].Value)), StringComparer.OrdinalIgnoreCase);
                foreach (string hook in new[] { entry.Entry, entry.Resume, entry.Init, entry.BeforeSave, entry.AfterLoad, entry.TurnEnd, entry.SaveCaption, entry.LegacyProbe })
                    if (hook.Length > 0 && !labels.Contains(hook)) throw new ArgumentException("登记的入口不在本剧本文件夹中：" + hook);
                candidates.Add(entry);
            }
            catch (Exception e) when (e is JsonException or ArgumentException or IOException or UnauthorizedAccessException or NullReferenceException)
            { catalog.Errors.Add(Path.GetRelativePath(root, file) + "：" + e.Message); }
        }
        foreach (var group in candidates.GroupBy(e => e.Id, StringComparer.OrdinalIgnoreCase))
            if (group.Count() != 1) catalog.Errors.Add("剧本编号重复，已停用：" + group.Key);
            else catalog.Entries.Add(group.Single());
        return catalog;
    }
    internal ScenarioDefinition Get(string id) => Entries.SingleOrDefault(e => e.Id == id) ?? throw new ArgumentException("未找到可用的剧本登记，请检查剧本文件夹。");
}
