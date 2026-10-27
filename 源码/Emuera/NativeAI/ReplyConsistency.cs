#nullable enable
using System;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace MinorShift.Emuera.NativeAI;

internal static class ReplyConsistency
{
    // A narrow review cue, never an inventory parser or an instruction executor.
    // Plans, quotations and past events are not proof of an acquisition.
    private static readonly Regex ExplicitQuantity = new(
        @"(?:找到|捡到|收下|买下|获得|拾得|用掉|消耗)(?:了)?\s*[0-9一二两三四五六七八九十百千万]+\s*(?:条|个|份|枚|把|支|发|块|瓶|颗|件|张|卷|根)",
        RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
    private static readonly string[] Uncertain = ["？", "?", "如果", "假如", "要是", "能否", "能不能", "打算", "计划", "想", "希望", "曾经", "以前", "昨天", "梦里", "没有", "没找到", "并未", "未曾", "不要", "别", "问", "“", "\"", "「"];
    internal static string MissingItemOperations(string player, string snapshot, string candidate)
    {
        if (Uncertain.Any(s => player.Contains(s, StringComparison.Ordinal)) || !ExplicitQuantity.IsMatch(player)) return "";
        var before = InventoryState.Read(snapshot);
        var after = InventoryState.Read(candidate);
        // Compare item records, not the common revision: a status-only reply also advances it.
        string oldItems = JsonSerializer.Serialize(before.Items, InventoryState.Json);
        string newItems = JsonSerializer.Serialize(after.Items, InventoryState.Json);
        if (oldItems != newItems) return "";
        return "物品核对：输入涉及明确数量的获得或消耗，但本轮没有物品变更。若剧情已确认发生，请取消本轮后重试；若只是提及、计划或没有实际发生，可继续采用。";
    }

    internal static string MissingStoryChanges(string story, string snapshot, string candidate)
    {
        var before = InventoryState.Read(snapshot);
        var after = InventoryState.Read(candidate);
        var notices = new System.Collections.Generic.List<string>();
        // These are review cues only. No quantity, condition or location is inferred or executed.
        if (ExplicitQuantity.IsMatch(story) && JsonSerializer.Serialize(before.Items, InventoryState.Json) == JsonSerializer.Serialize(after.Items, InventoryState.Json))
            notices.Add("剧情提及明确数量的获得或消耗，但没有物品变更");
        if (Regex.IsMatch(story, @"(?:新增|新添|造成|留下)(?:了)?[^，。！？]{0,10}(?:伤口|伤势)|(?:伤口|伤势)(?:已经|已|完全)(?:痊愈|愈合)", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100)) &&
            JsonSerializer.Serialize(before.Statuses, InventoryState.Json) == JsonSerializer.Serialize(after.Statuses, InventoryState.Json))
            notices.Add("剧情可能描述了伤势变化，但没有人物状态变更");
        if (Regex.IsMatch(story, @"(?:你|主角)(?:独自|随后|便|又|已经|终于|缓缓|一人|悄悄){0,3}(?:走到|来到|抵达|返回|回到)[^，。！？]{1,30}", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100)) &&
            JsonSerializer.Serialize(before.Scene, InventoryState.Json) == JsonSerializer.Serialize(after.Scene, InventoryState.Json))
            notices.Add("剧情可能描述了地点移动，但当前场景未变");
        return notices.Count == 0 ? "" : "结算核对：" + string.Join("；", notices) + "。请核对本轮是否只是提及；若确实漏记，请取消后重新输入。";
    }
}
