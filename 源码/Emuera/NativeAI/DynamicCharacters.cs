#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;

namespace MinorShift.Emuera.NativeAI;

// Newly encountered people belong to this save, never to the scenario's initial cards.
internal sealed class DynamicCharacter
{
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public string CreatedAt { get; set; } = "";
}

internal sealed partial class InventoryState
{
    public Dictionary<string, DynamicCharacter> DynamicCharacters { get; set; } = new();
    public int NextCharacterId { get; set; } = 1;

    internal string AvailableCharacterId()
    {
        int next = NextCharacterId;
        while (Characters.ContainsKey("npc_" + next.ToString(CultureInfo.InvariantCulture)) ||
               Owners.ContainsKey("npc_" + next.ToString(CultureInfo.InvariantCulture))) next = checked(next + 1);
        return "npc_" + next.ToString(CultureInfo.InvariantCulture);
    }

    private void ValidateDynamicCharacters()
    {
        if (DynamicCharacters == null || Characters == null || Owners == null || DynamicCharacters.Count > 128 || NextCharacterId < 1 || NextCharacterId == int.MaxValue)
            throw new ArgumentException("本局新增人物表无效。");
        foreach (var (id, person) in DynamicCharacters)
        {
            Owner(id);
            if (person == null || !Characters.TryGetValue(id, out string? name) || name != person.Name ||
                !Owners.TryGetValue(id, out string? owner) || owner != name)
                throw new ArgumentException("本局新增人物与人物、归属登记不一致。");
            Text(person.Name, 80, true); Text(person.Description, 1200, true); Text(person.CreatedAt, 160, false);
        }
    }
}

internal static partial class InventoryReply
{
    private static string CharacterInstruction() => """
新遇到并需要后续互动的具名人物用可选character_ops登记；普通路人不必建卡。格式："character_ops":[{"op":"create_character","character":"复制program_context.next_character_id","name":"人物姓名","description":"已知的身份、外观和性格，不猜未知身世或身体数值","location":"已知地点名称","reason":"本轮首次相识的依据"}]。
每轮最多新增1人，编号复制next_character_id，已有同名人物沿用原编号。新人默认次要人物，description用120字以内简述身份、一两个特征及与主角的关系；不补完整身世和未知身体数值。主要/次要由玩家设置，AI不能更改。人物登记后可在场景、归属和状态中引用；当前地点新人须列入scene_update.present_characters。新人物随本轮结算确认后保存；伤势、位置另记。无新人时省略。
""";

    private static int ApplyCharacters(InventoryExchange context, JsonElement root, InventoryState state,
        List<string> changes, List<string> rationale)
    {
        if (!root.TryGetProperty("character_ops", out var operations)) return 0;
        if (operations.ValueKind != JsonValueKind.Array || operations.GetArrayLength() > 1)
            throw new ArgumentException("每轮最多登记1位重要新人物。");
        if (context.Action != null && operations.GetArrayLength() > 0)
            throw new ArgumentException("程序行动不能追加人物登记。");
        foreach (var op in operations.EnumerateArray())
        {
            Fields(op, "op", "character", "name", "description", "location", "reason");
            if (String(op, "op", 32) != "create_character") throw new ArgumentException("不支持的人物登记操作。");
            string id = String(op, "character", 64), name = String(op, "name", 80).Trim();
            if (id != state.AvailableCharacterId()) throw new ArgumentException("新增人物编号与当前可用编号不一致。");
            if (state.Characters.Count >= 256 || state.Owners.Count >= 256 || state.DynamicCharacters.Count >= 128)
                throw new ArgumentException("人物登记数量已达上限。");
            if (state.Characters.Values.Any(n => n.Trim().Equals(name, StringComparison.OrdinalIgnoreCase)))
                throw new ArgumentException("已有同名人物，请沿用原人物编号。");
            string location = state.CanonicalLocation(String(op, "location", 120));
            if (state.Scene?.Location != location && !state.KnownScenes.Any(s => s.Name == location))
                throw new ArgumentException("新增人物所在地尚未登记。");
            string description = String(op, "description", 1200), reason = String(op, "reason", 300);
            state.Characters.Add(id, name); state.Owners.Add(id, name);
            state.DynamicCharacters.Add(id, new() { Name = name, Description = description, CreatedAt = context.GameTime });
            state.CharacterImportance[id] = "minor";
            state.CharacterLocations[id] = location;
            state.NextCharacterId = checked(int.Parse(id.AsSpan(4), CultureInfo.InvariantCulture) + 1);
            // Joining the current scene must be explicit in the same reviewed proposal.
            if (location == state.Scene?.Location &&
                (!root.TryGetProperty("scene_update", out var scene) || !scene.TryGetProperty("present_characters", out var present) ||
                 present.ValueKind != JsonValueKind.Array || !present.EnumerateArray().Any(v => v.ValueKind == JsonValueKind.String && v.GetString() == id)))
                throw new ArgumentException("当前地点的新人物须在本轮场景的在场人物中登记。");
            changes.Add("登记新人物：" + name + " / " + location);
            rationale.Add("人物资料：" + description + "\n登记依据：" + reason);
        }
        return operations.GetArrayLength();
    }
}
