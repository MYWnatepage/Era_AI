#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MinorShift.Emuera.NativeAI;

// Mutable conditions belong to the save, not the initial character portrait/card.
internal sealed class CharacterStatus
{
    [JsonRequired] public int Id { get; set; }
    [JsonRequired] public string Character { get; set; } = "";
    [JsonRequired] public string Name { get; set; } = "";
    [JsonRequired] public string Category { get; set; } = "";
    [JsonRequired] public string Severity { get; set; } = "";
    [JsonRequired] public string Description { get; set; } = "";
    [JsonRequired] public string Effects { get; set; } = "";
    [JsonRequired] public string CreatedAt { get; set; } = "";
    [JsonRequired] public string UpdatedAt { get; set; } = "";
}

internal sealed partial class InventoryState
{
    public Dictionary<string, string> Characters { get; set; } = new();
    public List<CharacterStatus> Statuses { get; set; } = [];
    public int NextStatusId { get; set; } = 1;

    internal void BindCharacters(IReadOnlyDictionary<string, string> characters)
    {
        var merged = characters.ToDictionary(p => p.Key, p => p.Value);
        foreach (var (id, person) in DynamicCharacters)
        {
            if (merged.ContainsKey(id)) throw new InvalidOperationException("剧本人物编号与本局新增人物冲突，未覆盖数据。");
            merged.Add(id, person.Name);
        }
        if (Statuses.Any(s => !merged.ContainsKey(s.Character)))
            throw new InvalidOperationException("存档状态包含当前剧本未登记的人物，未覆盖数据。");
        Characters = merged;
        ValidateStatuses();
    }
    private void ValidateStatuses()
    {
        if (Characters == null || Characters.Count > 256 || Statuses == null || Statuses.Count > 128 || NextStatusId < 1)
            throw new ArgumentException("人物状态表无效。");
        foreach (var pair in Characters) { Owner(pair.Key); Text(pair.Value, 80, true); }
        var ids = new HashSet<int>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var status in Statuses)
        {
            if (status == null || status.Id < 1 || status.Id >= NextStatusId || !ids.Add(status.Id) || !Characters.ContainsKey(status.Character))
                throw new ArgumentException("人物状态编号或所属人物无效。");
            Text(status.Name, 80, true); Text(status.Category, 40, true); Text(status.Severity, 40, true);
            Text(status.Description, 1000, true); Text(status.Effects, 500, false);
            Text(status.CreatedAt, 160, false); Text(status.UpdatedAt, 160, false);
            if (!names.Add(status.Character + ":" + status.Category.Trim() + ":" + status.Name.Trim())) throw new ArgumentException("同一人物存在同类同名状态。");
        }
        ValidateFatigueHistory();
    }
    internal CharacterStatus Status(int id) => Statuses.Find(s => s.Id == id) ?? throw new ArgumentException("指定人物状态不存在。");
    internal string StatusPrompt() => "当前人物状态（这是存档中的有效状态；空列表表示尚无已记录状态）：" +
        JsonSerializer.Serialize(Statuses.Select(s => new { id = s.Id, character = s.Character, name = s.Name,
            category = s.Category, severity = s.Severity, description = s.Description, effects = s.Effects }), Json);
    internal string[] StatusPage(int page)
    {
        int pages = Math.Max(1, (Statuses.Count + 9) / 10);
        page = Math.Clamp(page, 0, pages - 1);
        var lines = Enumerable.Repeat("", 12).ToArray();
        int index = 0;
        foreach (var status in Statuses.OrderBy(s => s.Id).Skip(page * 10).Take(10))
            lines[index++] = $"[{status.Id}] {Characters[status.Character]}    {status.Name} / {status.Severity}";
        lines[10] = pages.ToString(); lines[11] = Statuses.Count.ToString();
        return lines;
    }
}

internal static partial class InventoryReply
{
    private static string StatusInstruction(InventoryExchange context) => """
人物持续状态使用state_ops，以下仅为格式示例：
{"op":"create_status","character":"人物编号","name":"状态名称","category":"伤势","severity":"轻微","description":"伤口现状及处理进展","effects":"当前影响，可用空字符串","reason":"本轮依据"}
{"op":"update_status","status_id":1,"character":"原人物编号","name":"状态名称","category":"伤势","severity":"轻微","description":"更新后的完整现状","effects":"更新后的影响","reason":"本轮依据"}
{"op":"remove_status","status_id":1,"character":"原人物编号","reason":"明确痊愈或失效的依据"}
编号和发生时间由程序分配。同一处伤势沿用status_id更新，name可自然改写，不因换说法重建记录；指代不明时先询问。仅咨询处理方法时只给建议，实际处理后才结算材料与伤势；包扎不等于痊愈。物品和人物状态操作合计最多32项。
根据语境理解玩家的自然表达，不要求固定词或名称。否定、假设和治疗咨询不等于状态已发生。伤势、疾病另记，不因描述带疲惫而改成疲劳。
""" + (InventoryState.Read(context.Snapshot).FatiguePoints.HasValue
        ? "主角疲劳由程序按点数维护，state_ops不可创建、更新或移除主角疲劳；不可新增主角精力状态，既有旧精力记录可沿原类别更新或在确实消退后移除，不能与疲劳或其他类别互换；其他人物仍可记录自然疲劳状态。\n"
        : "普通劳累统一name和category为疲劳，同一人物只维护一条，程度用轻微、中度、重度。\n");

    private static int ApplyStatuses(InventoryExchange context, JsonElement root, InventoryState state, List<string> changes, List<string> rationale, int inventoryCount)
    {
        // Older compatible providers may omit this array; absence never clears existing conditions.
        if (!root.TryGetProperty("state_ops", out var operations)) return 0;
        if (operations.ValueKind != JsonValueKind.Array || operations.GetArrayLength() + inventoryCount > 32)
            throw new ArgumentException("人物状态操作列表无效或操作过多。");
        if (context.Action != null && operations.GetArrayLength() != 0)
            throw new ArgumentException("本次程序行动不允许追加人物状态变化。");
        foreach (var operation in operations.EnumerateArray())
        {
            string kind = String(operation, "op", 32), character = String(operation, "character", 64);
            if (!state.Characters.ContainsKey(character)) throw new ArgumentException("人物未在剧本中登记。");
            string reason = String(operation, "reason", 300);
            string label = state.Characters[character];
            if (kind == "remove_status")
            {
                Fields(operation, "op", "status_id", "character", "reason");
                var status = state.Status(Int(operation, "status_id"));
                if (status.Character != character) throw new ArgumentException("状态不属于指定人物。");
                if (character == "player" && state.FatiguePoints.HasValue && status.Category.Trim() == "疲劳")
                    throw new ArgumentException("主角疲劳由疲劳点维护，请通过实际休息恢复。");
                state.Statuses.Remove(status);
                state.FatigueHistory.Remove(status.Id);
                changes.Add($"{label}移除状态：{status.Name}");
            }
            else if (kind is "create_status" or "update_status")
            {
                if (kind == "create_status") Fields(operation, "op", "character", "name", "category", "severity", "description", "effects", "reason");
                else Fields(operation, "op", "status_id", "character", "name", "category", "severity", "description", "effects", "reason");
                string name = String(operation, "name", 80).Trim();
                string category = String(operation, "category", 40).Trim();
                bool fatigue = category == "疲劳";
                if (character == "player" && state.FatiguePoints.HasValue &&
                    (category == "疲劳" || kind == "create_status" && category == "精力"))
                    throw new ArgumentException("主角疲劳由程序根据活动强度计算，不能直接修改疲劳或新增精力状态。");
                if (fatigue) name = "疲劳";
                CharacterStatus status;
                bool created = false;
                if (kind == "create_status")
                {
                    // Fatigue is one current condition, not a pile of synonymous records.
                    // A fresh model create reuses its existing ID and current severity.
                    var existing = fatigue ? state.Statuses.FirstOrDefault(s => s.Character == character && s.Category.Trim() == "疲劳") : null;
                    if (existing != null) status = existing;
                    else
                    {
                        if (state.Statuses.Count >= 128) throw new ArgumentException("人物状态记录过多。");
                        status = new() { Id = state.NextStatusId, Character = character, CreatedAt = context.GameTime };
                        state.NextStatusId = checked(state.NextStatusId + 1);
                        state.Statuses.Add(status); created = true;
                    }
                }
                else
                {
                    status = state.Status(Int(operation, "status_id"));
                    if (status.Character != character) throw new ArgumentException("状态不属于指定人物。");
                    if (character == "player" && state.FatiguePoints.HasValue &&
                        (category == "精力" || status.Category.Trim() == "精力") && category != status.Category.Trim())
                        throw new ArgumentException("既有精力记录只能沿原类别维护，不能与疲劳或其他类别互换。");
                    if ((fatigue && status.Category.Trim() is not ("疲劳" or "精力")) ||
                        (!fatigue && status.Category.Trim() == "疲劳"))
                        throw new ArgumentException("已有疲劳和其他伤病不能互换类别，请沿用各自状态编号。");
                }
                if (state.Statuses.Any(s => s.Id != status.Id && s.Character == character && s.Category.Trim() == category && string.Equals(s.Name.Trim(), name, StringComparison.OrdinalIgnoreCase)))
                    throw new ArgumentException("同一人物已有同名状态，应更新原记录。");
                status.Name = name; status.Category = category; status.Severity = String(operation, "severity", 40).Trim();
                status.Description = String(operation, "description", 1000); status.Effects = String(operation, "effects", 500, false);
                status.UpdatedAt = context.GameTime;
                changes.Add($"{label}{(created ? "新增" : "更新")}状态：{status.Name} / {status.Severity}\n  {status.Description}" +
                    (status.Effects.Length > 0 ? "\n  当前影响：" + status.Effects : ""));
                rationale.Add($"状态资料：{status.Category} / {status.Severity} / {status.Description} / {status.Effects}");
            }
            else throw new ArgumentException("包含不支持的人物状态操作。");
            rationale.Add(reason);
        }
        return operations.GetArrayLength();
    }
}
