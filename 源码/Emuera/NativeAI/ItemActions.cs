#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace MinorShift.Emuera.NativeAI;

// This selection is constructed by the local item UI, never from model output or player prose.
internal sealed record ItemActionSelection(int ItemId, string Owner, string Mode, int? StatusId = null, int Roll = 0);

// A prepared plan holds only immutable values. It is a candidate until the whole reply is adopted.
internal sealed class ItemActionPlan
{
    internal ItemActionSelection Selection { get; }
    internal int BaseRevision { get; }
    internal string BaseTurnId { get; }
    internal string ItemName { get; }
    internal string Character { get; }
    internal string StatusName { get; }
    internal int Cost { get; }
    internal string Quality { get; }
    internal string Description { get; }
    internal string Effects { get; }
    internal string GameTime { get; }
    internal string ItemFingerprint { get; }
    internal string StatusFingerprint { get; }
    internal int EffectiveRoll { get; }
    internal int FatiguePenalty { get; }
    internal string RollSummary => Selection.Roll == 0 ? "无骰点判定" :
        FatigueDice.Explanation(Selection.Roll, EffectiveRoll, FatiguePenalty);
    internal bool IsTreatment => Selection.Mode == "bandage";

    internal ItemActionPlan(ItemActionSelection selection, InventoryState state, InventoryItem item,
        CharacterStatus? status, int cost, string quality, string description, string effects, string gameTime,
        int effectiveRoll = 0, int fatiguePenalty = 0)
    {
        Selection = selection; BaseRevision = state.Revision; BaseTurnId = state.LastTurnId;
        ItemName = item.Name; Character = status?.Character ?? ""; StatusName = status?.Name ?? "";
        Cost = cost; Quality = quality; Description = description; Effects = effects; GameTime = gameTime;
        ItemFingerprint = JsonSerializer.Serialize(item, InventoryState.Json);
        StatusFingerprint = status == null ? "" : JsonSerializer.Serialize(status, InventoryState.Json);
        EffectiveRoll = selection.Roll == 0 ? 0 : effectiveRoll > 0 ? effectiveRoll : selection.Roll;
        FatiguePenalty = fatiguePenalty;
    }
}

internal static class ItemActions
{
    // Names/tags are only an eligibility check after the player explicitly chooses the known use.
    // Ordinary free input can never enter this path. Scenarios may give dynamic items an explicit
    // 用途：包扎 tag, while all detailed narrative attributes remain inert text.
    internal static bool CanBandage(InventoryItem item)
    {
        if (new[] { "绷带", "布条", "敷料", "纱布" }.Any(word => item.Name.Contains(word, StringComparison.Ordinal))) return true;
        string attributes = item.Attributes.Replace(" ", "", StringComparison.Ordinal).Replace(':', '：');
        return attributes.Split(new[] { '；', ';', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries)
            .Any(tag => tag is "用途：包扎" or "用法：包扎" or "可用作：包扎" or "use：bandage");
    }

    internal static ItemActionPlan Prepare(InventoryState state, ItemActionSelection selection, string gameTime = "", ScenarioDefinition? scenario = null)
    {
        _ = state.Snapshot();
        if (selection == null) throw new ArgumentException("未选择物品。");
        state.RequireOwner(selection.Owner);
        if (!state.Characters.ContainsKey(selection.Owner) || !Present(state, selection.Owner))
            throw new ArgumentException("只能关联当前在场人物持有的物品。");
        var item = state.Item(selection.ItemId);
        if (item.Quantities.GetValueOrDefault(selection.Owner) < 1) throw new ArgumentException("选中归属没有可用的该物品。");
        InventoryState.Text(gameTime, 160, false);
        if (selection.Mode == "context")
        {
            if (selection.StatusId != null || selection.Roll != 0) throw new ArgumentException("关联物品不能附带消耗、目标状态或骰点。");
            return new(selection, state, item, null, 0, "仅关联资料", "", "", gameTime);
        }
        if (selection.Mode != "bandage") throw new ArgumentException("未知的物品用法。");
        if (selection.Owner != "player") throw new ArgumentException("执行包扎须先取得主角持有的材料。");
        if (!CanBandage(item)) throw new ArgumentException("所选物品没有包扎用途，请选择布条、绷带或带明确包扎用途的材料。");
        if (selection.StatusId is not { } statusId) throw new ArgumentException("包扎需要明确选择已有的伤势。");
        if (selection.Roll < 0 || selection.Roll > 20) throw new ArgumentException("包扎骰点必须为0（无判定）或D20的1至20。");
        var status = state.Status(statusId);
        if (!state.Characters.ContainsKey(status.Character) || !Present(state, status.Character))
            throw new ArgumentException("包扎对象当前不在场。");
        if (status.Category != "伤势") throw new ArgumentException("所选状态不是可包扎的伤势。");
        if (new[] { "已包扎", "完成包扎", "包扎完成", "包扎后", "[程序包扎]" }
            .Any(word => (status.Description + "\n" + status.Effects).Contains(word, StringComparison.Ordinal)))
            throw new ArgumentException("该伤势已有包扎记录，不会重复消耗材料；需要换药时先明确更新伤势状态。");
        int penalty = selection.Roll == 0 ? 0 : scenario == null ? FatigueSystem.Penalty(state) : FatigueSystem.Penalty(state, scenario);
        int effectiveRoll = selection.Roll == 0 ? 0 : FatigueDice.Effective(selection.Roll, penalty);
        string quality = effectiveRoll switch { 0 => "常规包扎", <= 5 => "包扎欠稳，需留意松脱", <= 14 => "基本包扎", _ => "包扎稳妥" };
        string description = status.Description + $"\n[程序包扎] 使用{item.Name}完成包扎，{quality}；伤势尚未痊愈。";
        string effects = status.Effects + (status.Effects.Length > 0 ? "\n" : "") +
            $"[程序包扎] {quality}，仍保留原伤势影响，需后续观察。";
        // Treatment must never silently erase the tail of an existing injury record.
        // Validate before a paid request, rather than shortening authoritative save data.
        if (description.Length > 1000 || effects.Length > 500)
            throw new ArgumentException("当前伤势描述或影响已接近长度上限，无法完整附加包扎记录；本次未使用材料。请先精简该伤势记录后再包扎。");
        InventoryState.Text(description, 1000, true);
        InventoryState.Text(effects, 500, false);
        return new(selection, state, item, status, 1, quality, description, effects, gameTime, effectiveRoll, penalty);
    }

    internal static string Prompt(ItemActionPlan plan) => JsonSerializer.Serialize(new
    {
        mode = plan.Selection.Mode, item_id = plan.Selection.ItemId, owner = plan.Selection.Owner,
        item_name = plan.ItemName, status_id = plan.Selection.StatusId,
        character = plan.Character, status_name = plan.StatusName,
        cost = plan.Cost, roll = plan.EffectiveRoll, raw_roll = plan.Selection.Roll,
        fatigue_penalty = plan.FatiguePenalty, sides = plan.Selection.Roll == 0 ? 0 : 20,
        result = plan.Quality, description = plan.Description, effects = plan.Effects,
        instruction = plan.IsTreatment
            ? "玩家已明确选择包扎用法及伤势，消耗与包扎现状由程序待结算。按此续写完整剧情，不为选中物品追加任何物品操作，不新增、修改或移除同一伤势，不宣称痊愈，不改变骰点；其他实际发生的无关变化仍按协议提出。"
            : "玩家只关联了物品资料，尚未授权预定消耗或效果。结合player_input判断行动，检查、谈论或计划不消耗；实际发生的变化按正常协议提出并等待确认。",
    }, InventoryState.Json);

    internal static void ValidateModelOperations(ItemActionPlan plan, JsonElement root)
    {
        if (!plan.IsTreatment) return;
        if (root.TryGetProperty("inventory_ops", out var items) && items.ValueKind == JsonValueKind.Array)
            foreach (var op in items.EnumerateArray())
                if (op.ValueKind == JsonValueKind.Object && op.TryGetProperty("item_id", out var id) &&
                    id.ValueKind == JsonValueKind.Number && id.TryGetInt32(out int itemId) && itemId == plan.Selection.ItemId)
                    throw new ArgumentException("包扎材料由程序结算，模型不能再次改变或转交同一物品。");
        if (root.TryGetProperty("state_ops", out var statuses) && statuses.ValueKind == JsonValueKind.Array)
            foreach (var op in statuses.EnumerateArray())
            {
                if (op.ValueKind != JsonValueKind.Object) continue;
                bool sameId = op.TryGetProperty("status_id", out var id) && id.ValueKind == JsonValueKind.Number &&
                    id.TryGetInt32(out int statusId) && statusId == plan.Selection.StatusId;
                bool sameNamed = op.TryGetProperty("character", out var character) && character.ValueKind == JsonValueKind.String &&
                    character.GetString() == plan.Character && op.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String &&
                    string.Equals(name.GetString()?.Trim(), plan.StatusName.Trim(), StringComparison.OrdinalIgnoreCase);
                if (sameId || sameNamed) throw new ArgumentException("选中伤势的包扎结果由程序结算，模型不能覆盖或重复创建该状态。");
            }
    }

    internal static void Apply(ItemActionPlan plan, InventoryState state, List<string> changes, List<string> rationale)
    {
        if (!plan.IsTreatment) return;
        if (state.Revision != plan.BaseRevision || state.LastTurnId != plan.BaseTurnId)
            throw new ArgumentException("选中物品的行动计划已过期，请重新选择。");
        var item = state.Item(plan.Selection.ItemId);
        if (JsonSerializer.Serialize(item, InventoryState.Json) != plan.ItemFingerprint)
            throw new ArgumentException("选中物品在本轮被重复改动，未提交该行动。");
        var status = state.Status(plan.Selection.StatusId!.Value);
        if (JsonSerializer.Serialize(status, InventoryState.Json) != plan.StatusFingerprint)
            throw new ArgumentException("选中伤势在本轮被重复改动，未提交该行动。");
        int quantity = item.Quantities.GetValueOrDefault(plan.Selection.Owner);
        if (quantity < plan.Cost) throw new ArgumentException("包扎材料不足，未提交该行动。");
        // Do not alter Revision here: the caller increments once after every candidate operation passes.
        item.Quantities[plan.Selection.Owner] = quantity - plan.Cost;
        status.Description = plan.Description; status.Effects = plan.Effects;
        status.UpdatedAt = plan.GameTime;
        state.ContextItemIds = new[] { item.Id }.Concat(state.ContextItemIds).Distinct().Take(32).ToList();
        changes.Add($"{state.OwnerName(plan.Selection.Owner)}的{item.Name} -{plan.Cost}");
        changes.Add($"{state.Characters[status.Character]}的{status.Name}：{plan.Quality}，尚未痊愈。");
        rationale.Add("玩家明确选择包扎用法与已有伤势；程序确定材料消耗和处理结果" +
            "（" + plan.RollSummary + "）。");
    }

    private static bool Present(InventoryState state, string character) => character == "player" ||
        (state.Scene?.PresentCharacters.Contains(character, StringComparer.Ordinal) ?? false);
}
