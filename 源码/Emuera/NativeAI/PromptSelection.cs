#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace MinorShift.Emuera.NativeAI;

// A scenario supplies vocabulary and dependencies. The engine has no built-in item names.
internal sealed class PromptSelectionRules
{
    public Dictionary<int, string[]> ItemAliases { get; set; } = new();
    public Dictionary<int, int[]> RelatedItems { get; set; } = new();
    public Dictionary<string, int[]> ActionItems { get; set; } = new();

    internal void Validate()
    {
        if (ItemAliases == null || ItemAliases.Count > InventoryState.MaxItems ||
            RelatedItems == null || RelatedItems.Count > InventoryState.MaxItems ||
            ActionItems == null || ActionItems.Count > 64)
            throw new ArgumentException("剧本上下文筛选规则数量无效。");
        int aliasCount = 0;
        foreach (var (id, aliases) in ItemAliases)
        {
            Id(id);
            if (aliases == null || aliases.Length is < 1 or > 16)
                throw new ArgumentException("物品别名数量无效。");
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string alias in aliases)
            {
                Term(alias, 80);
                if (!seen.Add(alias)) throw new ArgumentException("同一物品的别名不能重复。");
            }
            aliasCount += aliases.Length;
        }
        if (aliasCount > 2048) throw new ArgumentException("物品别名总数过多。");
        foreach (var (id, related) in RelatedItems) { Id(id); Ids(related); }
        var actions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (term, items) in ActionItems)
        {
            Term(term, 40);
            if ((term.Length == 1 && (term[0] < '\u3400' || term[0] > '\u9FFF')) || !actions.Add(term)) throw new ArgumentException("行动关键词过短或重复。");
            Ids(items);
        }
    }

    private static void Id(int id)
    {
        if (id < 1 || id == int.MaxValue) throw new ArgumentException("上下文规则中的物品编号无效。");
    }

    private static void Ids(int[]? values)
    {
        if (values == null || values.Length is < 1 or > 64 || values.Distinct().Count() != values.Length)
            throw new ArgumentException("上下文规则中的关联物品列表无效。");
        foreach (int value in values) Id(value);
    }

    private static void Term(string? term, int max)
    {
        InventoryState.Text(term, max, true);
        if (term != term!.Trim() || term.Any(char.IsControl))
            throw new ArgumentException("上下文关键词不能包含首尾空格或控制字符。");
    }
}

internal static class PromptSelection
{
    private static readonly string[] GenericNameTails =
        ["物品", "物件", "道具", "东西", "用品", "材料", "工具", "装备", "资源", "事情", "情况", "数量", "其他", "其它"];
    private static readonly Regex FullInventory = new(
        @"(?:查看|看看|看一下|列出|显示|展示|清点|盘点|检查|浏览)[^，。！？；\r\n]{0,12}(?:全部|所有|完整|整个)[^，。！？；\r\n]{0,6}(?:物品|库存|背包|装备)",
        RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
    private static readonly Regex DeniedFullInventory = new(@"(?:不要|不用|不必|无需|别|不想|不需要)[^，。！？；\r\n]{0,8}$",
        RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
    private static readonly Regex RecentReference = new(
        @"(?<!其)(?:它们|它(?=的|还|也|会|能|可|是|在|有|怎|要|呢|吧|吗|[，。！？；：、\s,!?;:]|$))" +
        @"|(?:把|将|拿起|拿着|用|使用|给|交给|检查|看看|放下|收好|丢掉|放回)它" +
        @"|(?:这|那)(?:件|把|根|条|枚|支|瓶|袋|块|双|对|本|盒)(?!事情)" +
        @"|(?:那个|这个|那些|这些)(?=$|呢|吧|吗|[，。！？；：、\s]|怎么样|怎么用|还能|能用|放在|拿来)" +
        @"|继续|接着|刚(?:才|刚)(?:的|找到|拿到|获得|得到)",
        RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));

    internal static IReadOnlyList<InventoryItem> Select(InventoryState state, string player,
        IReadOnlyList<ConversationTurn> recent, PromptSelectionRules? rules = null)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(player);
        ArgumentNullException.ThrowIfNull(recent);
        rules ??= new();
        rules.Validate();
        // Zero-quantity records still carry identity: a later acquisition reuses the same ID.
        var items = state.Items.ToDictionary(item => item.Id);
        if (RequestsFullInventory(player)) return items.Values.OrderBy(item => item.Id).ToArray();
        var chosen = new HashSet<int>();
        AddMentions(player);
        foreach (var (term, ids) in rules.ActionItems)
            if (ContainsTerm(player, term)) Add(ids);

        // Recent dialogue is a referent aid, not authority for quantities or possession.
        // An unrelated new conversation does not carry a previous inventory discussion along.
        if (RefersToRecentItems(player))
        {
            Add(state.ContextItemIds);
            for (int index = Math.Max(0, recent.Count - 2); index < recent.Count; index++)
            {
                AddMentions(recent[index].Player);
                AddMentions(recent[index].Reply);
            }
        }

        var dependencies = new Queue<int>(chosen);
        while (dependencies.TryDequeue(out int id))
            if (rules.RelatedItems.TryGetValue(id, out var related))
                foreach (int next in related)
                    if (items.ContainsKey(next) && chosen.Add(next)) dependencies.Enqueue(next);
        return chosen.OrderBy(id => id).Select(id => items[id]).ToArray();

        void Add(IEnumerable<int> ids)
        {
            foreach (int id in ids) if (items.ContainsKey(id)) chosen.Add(id);
        }
        void AddMentions(string text)
        {
            foreach (var item in items.Values)
                if (Mentions(item, text, rules)) chosen.Add(item.Id);
        }
    }

    internal static bool Mentions(InventoryItem item, string text, PromptSelectionRules? rules = null) =>
        ContainsTerm(text, item.Name) || ShortNames(item.Name).Any(name => ContainsTerm(text, name)) ||
        (rules?.ItemAliases.TryGetValue(item.Id, out var aliases) == true && aliases.Any(alias => ContainsTerm(text, alias)));

    internal static bool RefersToRecentItems(string player) => RecentReference.IsMatch(player);

    internal static bool RequestsFullInventory(string text)
    {
        foreach (Match match in FullInventory.Matches(text))
        {
            string before = text.Substring(Math.Max(0, match.Index - 16), Math.Min(match.Index, 16));
            if (!DeniedFullInventory.IsMatch(before)) return true;
        }
        return false;
    }

    private static IEnumerable<string> ShortNames(string name)
    {
        // Newly generated names have no scenario-authored aliases. Keep conservative two/three
        // character Chinese suffixes as candidates, e.g. a qualified name may be called by its noun.
        // A match never resolves ambiguity: all matching records still enter the local selection.
        int end = name.Length;
        while (end > 0 && char.IsWhiteSpace(name[end - 1])) end--;
        int start = end;
        while (start > 0 && Han(name[start - 1])) start--;
        for (int length = 2; length <= 3 && length <= end - start; length++)
        {
            if (start == 0 && length == end) continue; // The full name is matched separately.
            string fragment = name.Substring(end - length, length);
            if (fragment.Any(c => c is '的' or '之' or '与' or '和') ||
                GenericNameTails.Any(tail => fragment.EndsWith(tail, StringComparison.Ordinal))) continue;
            yield return fragment;
        }
    }

    private static bool Han(char value) => value is >= '\u3400' and <= '\u4DBF' or >= '\u4E00' and <= '\u9FFF';

    private static bool ContainsTerm(string text, string term)
    {
        if (term.Length == 0) return false;
        int start = 0;
        while (start < text.Length)
        {
            int index = text.IndexOf(term, start, StringComparison.OrdinalIgnoreCase);
            if (index < 0) return false;
            int end = index + term.Length;
            if (!(AsciiWord(term[0]) && index > 0 && AsciiWord(text[index - 1])) &&
                !(AsciiWord(term[^1]) && end < text.Length && AsciiWord(text[end]))) return true;
            start = index + 1;
        }
        return false;
    }

    private static bool AsciiWord(char value) => value is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '_';
}
