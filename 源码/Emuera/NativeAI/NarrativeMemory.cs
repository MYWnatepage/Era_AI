#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MinorShift.Emuera.NativeAI;

// This is a save-bound archive, never a replacement for inventory, conditions or location.
internal sealed class NarrativeMemory
{
    [JsonRequired] public int Id { get; set; }
    [JsonRequired] public string Time { get; set; } = "";
    [JsonRequired] public string Summary { get; set; } = "";
    [JsonRequired] public List<string> Keywords { get; set; } = [];
    [JsonRequired] public string Source { get; set; } = "player";
    [JsonRequired] public bool Active { get; set; } = true;
    [JsonRequired] public bool Pinned { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public int? DueDay { get; set; }
}

internal sealed partial class InventoryState
{
    internal const int MaxMemories = 256;
    internal const int MaxMemoryProposals = 32;
    internal const int MaxPinnedMemories = 4;
    public List<NarrativeMemory> Memories { get; set; } = [];
    public List<NarrativeMemory> MemoryProposals { get; set; } = [];
    public int NextMemoryId { get; set; } = 1;

    private void ValidateMemories()
    {
        if (Memories == null || MemoryProposals == null || Memories.Count > MaxMemories ||
            MemoryProposals.Count > MaxMemoryProposals || NextMemoryId < 1 || NextMemoryId == int.MaxValue ||
            Memories.Count(m => m != null && m.Pinned) > MaxPinnedMemories)
            throw new ArgumentException("历史记忆列表、编号或置顶数量无效。");
        var ids = new HashSet<int>();
        foreach (var memory in Memories.Concat(MemoryProposals))
        {
            if (memory == null || memory.Id < 1 || memory.Id >= NextMemoryId || !ids.Add(memory.Id) ||
                memory.Source is not ("player" or "ai"))
                throw new ArgumentException("历史记忆编号或来源无效。");
            Text(memory.Time, 160, false); Text(memory.Summary, 500, true);
            if (memory.DueDay is <= 0) throw new ArgumentException("约定日期须为大于0的游戏日。");
            if (memory.Keywords == null || memory.Keywords.Count > 8 ||
                memory.Keywords.Distinct(StringComparer.OrdinalIgnoreCase).Count() != memory.Keywords.Count)
                throw new ArgumentException("历史记忆关键词无效。");
            foreach (string keyword in memory.Keywords) Text(keyword, 40, true);
        }
        if (MemoryProposals.Any(m => m.Source != "ai" || m.Pinned || !m.Active))
            throw new ArgumentException("待确认记忆的来源或状态无效。");
    }

    internal object[] MemoryPrompt(string player, string gameTime, SceneState? scene)
    {
        // Short, whole-keyword matches keep unrelated old events off the request.
        string context = player + "\n" + gameTime + "\n" + (scene?.Location ?? "");
        // A person's mere presence must not recall every old event about that person.
        // Explicit names in player input still match normally.
        int Rank(NarrativeMemory m)
        {
            int score = m.Keywords.Where(k => k.Length >= 2).Sum(k => context.Contains(k, StringComparison.OrdinalIgnoreCase) ? 4 : 0);
            foreach (string name in Characters.Values.Where(n => n.Length >= 2))
                if (player.Contains(name, StringComparison.Ordinal) && m.Summary.Contains(name, StringComparison.Ordinal)) score += 1;
            return score;
        }
        // Explicit dates are save data, never guessed from a prose summary. An expired
        // date does not prove completion. Inactive memories remain local even if pinned.
        int? day = GameDay(gameTime);
        var picked = Memories.Where(m => m.Active).Select(m => new { Memory = m, Rank = Rank(m),
                Due = day.HasValue && m.DueDay.HasValue && m.DueDay.Value <= day.Value })
            .Where(x => x.Memory.Pinned || x.Rank > 0 || x.Due).OrderByDescending(x => x.Memory.Pinned)
            .ThenByDescending(x => x.Due).ThenByDescending(x => x.Rank)
            .ThenBy(x => x.Memory.DueDay ?? int.MaxValue).ThenByDescending(x => x.Memory.Id);
        int length = 0, dateOnly = 0;
        var result = new List<object>();
        foreach (var entry in picked)
        {
            if (result.Count >= 6) break;
            bool dueOnly = !entry.Memory.Pinned && entry.Rank == 0 && entry.Due;
            if (dueOnly && dateOnly >= 2) continue;
            if (!entry.Memory.Pinned && length + entry.Memory.Summary.Length > 2400) continue;
            if (dueOnly) dateOnly++;
            length += entry.Memory.Summary.Length;
            var view = new Dictionary<string, object> { ["id"] = entry.Memory.Id, ["time"] = entry.Memory.Time,
                ["summary"] = entry.Memory.Summary, ["source"] = entry.Memory.Source, ["pinned"] = entry.Memory.Pinned };
            if (entry.Memory.DueDay.HasValue) { view["due_day"] = entry.Memory.DueDay.Value; view["due"] = entry.Due; }
            result.Add(view);
        }
        return result.ToArray();
    }

    private static int? GameDay(string gameTime)
    {
        int start = gameTime.IndexOf('第'), end = start < 0 ? -1 : gameTime.IndexOf('天', start + 1);
        return start >= 0 && end > start && int.TryParse(gameTime.AsSpan(start + 1, end - start - 1).Trim(),
            NumberStyles.None, CultureInfo.InvariantCulture, out int day) && day > 0 ? day : null;
    }
}

internal static partial class InventoryReply
{
    internal static string MemoryInstruction() => """
长期记忆可选字段memory_proposals，格式示例："memory_proposals":[{"summary":"主角答应次日上午与同伴去集市，这是尚未履行的约定。","keywords":["同伴","集市","约定"]}]。
每轮最多3条，仅建议需要长期保留的新事件、约定或线索；summary为1至500字文字，keywords为最多8个不超过40字的关键词。没有重要新记忆时省略或返回空数组。不要复制整段剧情，不把意图写成已完成结果。
明确履行日期的约定可附加due_day（大于0的游戏日整数，如第4天为4）；只有日期明确且能由game_time确定时填写，不清楚则省略，不从现实日期推测。到期不等于完成；已提供记忆的due仅表示日期已到，不能据此虚构履约。
建议需要玩家另外确认，尚未成为已确认记忆。不得用记忆建议替代物品、人物状态或场景结算；已提供的long_term_memories只补充历史背景，当前账本优先。
""";

    private static int ApplyMemories(InventoryExchange context, JsonElement root, InventoryState state,
        List<string> changes, List<string> rationale)
    {
        // Optional editorial suggestions cannot invalidate otherwise valid game settlement.
        // Parse in an isolated candidate so a bad second proposal cannot keep the first.
        var candidate = new InventoryState { Memories = [.. state.Memories], MemoryProposals = [.. state.MemoryProposals], NextMemoryId = state.NextMemoryId };
        var proposedChanges = new List<string>();
        var proposedRationale = new List<string>();
        try
        {
            int count = ParseMemoryProposals(context, root, candidate, proposedChanges, proposedRationale);
            state.MemoryProposals = candidate.MemoryProposals; state.NextMemoryId = candidate.NextMemoryId;
            changes.AddRange(proposedChanges); rationale.AddRange(proposedRationale);
            return count;
        }
        catch (Exception e) when (e is ArgumentException or OverflowException)
        {
            rationale.Add("本轮记忆建议格式无效，整批建议未保存；其他已通过校验的剧情和结算可照常采用。" + e.Message);
            return 0;
        }
    }

    private static int ParseMemoryProposals(InventoryExchange context, JsonElement root, InventoryState state,
        List<string> changes, List<string> rationale)
    {
        if (!root.TryGetProperty("memory_proposals", out var proposals)) return 0;
        if (proposals.ValueKind != JsonValueKind.Array || proposals.GetArrayLength() > 3)
            throw new ArgumentException("memory_proposals须为最多3条的记忆建议数组。");
        int added = 0;
        foreach (var proposal in proposals.EnumerateArray())
        {
            bool hasDueDay = proposal.ValueKind == JsonValueKind.Object && proposal.TryGetProperty("due_day", out _);
            Fields(proposal, hasDueDay ? ["summary", "keywords", "due_day"] : ["summary", "keywords"]);
            int? dueDay = null;
            if (hasDueDay && proposal.GetProperty("due_day") is var dueValue && dueValue.ValueKind != JsonValueKind.Null)
            {
                if (dueValue.ValueKind != JsonValueKind.Number || !dueValue.TryGetInt32(out int date) || date < 1)
                    throw new ArgumentException("记忆建议due_day须为大于0的游戏日整数，未知时省略。");
                dueDay = date;
            }
            string summary = String(proposal, "summary", 500).Trim();
            var keywords = proposal.GetProperty("keywords");
            if (keywords.ValueKind != JsonValueKind.Array || keywords.GetArrayLength() > 8)
                throw new ArgumentException("记忆建议keywords须为最多8项的文字数组。");
            var parsed = keywords.EnumerateArray().Select(value =>
            {
                if (value.ValueKind != JsonValueKind.String) throw new ArgumentException("记忆建议关键词必须是文字。");
                string keyword = value.GetString()!.Trim();
                InventoryState.Text(keyword, 40, true);
                return keyword;
            }).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (state.Memories.Concat(state.MemoryProposals).Any(m => m.Summary.Trim() == summary)) continue;
            if (state.MemoryProposals.Count >= InventoryState.MaxMemoryProposals)
            {
                if (!rationale.Contains("待确认记忆已满，本轮额外建议未加入；请先在历史记忆页处理旧建议。"))
                    rationale.Add("待确认记忆已满，本轮额外建议未加入；请先在历史记忆页处理旧建议。");
                continue;
            }
            var memory = new NarrativeMemory { Id = state.NextMemoryId, Time = context.GameTime, Summary = summary,
                Keywords = parsed, Source = "ai", DueDay = dueDay };
            if (state.NextMemoryId >= int.MaxValue - 1) throw new ArgumentException("历史记忆编号已达上限。");
            state.NextMemoryId = checked(state.NextMemoryId + 1);
            state.MemoryProposals.Add(memory); added++;
        }
        if (added > 0) changes.Add($"新增 {added} 条待确认记忆建议；需要在历史记忆页另行采用，尚不参与后续剧情。");
        return added;
    }
}

// All commands work on a candidate copy. Slot 0 is always the complete new snapshot.
internal static class MemoryCommands
{
    internal static string[] Execute(string snapshot, int action, string[] args) => Execute(InventoryState.Read(snapshot), action.ToString(CultureInfo.InvariantCulture), args);
    internal static string[] Execute(InventoryState original, string action, string[] args)
    {
        var state = InventoryState.Read(original.Snapshot());
        var result = Enumerable.Repeat("", 24).ToArray();
        string Arg(int i) => i < args.Length ? args[i] : "";
        int Number(int i) => int.TryParse(Arg(i), NumberStyles.Integer, CultureInfo.InvariantCulture, out int n)
            ? n : throw new ArgumentException("请选择有效的历史记忆编号。");
        NarrativeMemory Find(int id) => state.Memories.Concat(state.MemoryProposals).FirstOrDefault(m => m.Id == id)
            ?? throw new ArgumentException("指定历史记忆不存在。");
        List<string> Keywords(string input)
        {
            var words = input.Split([',', '，', '、', ';', '；', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (words.Count > 8) throw new ArgumentException("最多填写8个关键词。");
            foreach (string word in words) InventoryState.Text(word, 40, true);
            return words;
        }
        bool mutated = true;
        switch (action)
        {
            case "70": case "list":
                bool pending = Arg(1) == "1";
                var source = (pending ? state.MemoryProposals : state.Memories).OrderByDescending(m => m.Pinned).ThenByDescending(m => m.Id).ToArray();
                int pages = Math.Max(1, (source.Length + 9) / 10), page = Math.Clamp(Number(0), 0, pages - 1);
                result[1] = pages.ToString(CultureInfo.InvariantCulture); result[2] = source.Length.ToString(CultureInfo.InvariantCulture);
                int row = 0;
                foreach (var memory in source.Skip(page * 10).Take(10))
                {
                    string summary = memory.Summary.Replace('\n', ' ').Replace('\r', ' ');
                    if (summary.Length > 54) summary = summary[..54] + "…";
                    string due = memory.DueDay.HasValue ? $" / 约定第{memory.DueDay.Value}天" : "";
                    result[3 + row] = $"[{row + 1}] {(memory.Pinned ? "置顶 / " : "")}{(!memory.Active ? "失效 / " : "")}{memory.Time}{due}  {summary}";
                    result[13 + row] = memory.Id.ToString(CultureInfo.InvariantCulture); row++;
                }
                mutated = false; break;
            case "71": case "detail":
                var detail = Find(Number(0));
                result[1] = detail.Summary; result[2] = detail.Time; result[3] = string.Join("，", detail.Keywords);
                result[4] = detail.Source; result[5] = detail.Active ? "1" : "0"; result[6] = detail.Pinned ? "1" : "0";
                result[7] = state.MemoryProposals.Contains(detail) ? "1" : "0"; result[8] = detail.Id.ToString(CultureInfo.InvariantCulture);
                result[9] = detail.DueDay?.ToString(CultureInfo.InvariantCulture) ?? "";
                mutated = false; break;
            case "72": case "add":
                if (state.Memories.Count >= InventoryState.MaxMemories) throw new ArgumentException("历史记忆已达256条，请先清理不再需要的条目。");
                state.Memories.Add(new NarrativeMemory { Id = state.NextMemoryId++, Summary = Arg(0).Trim(), Time = Arg(1).Trim(), Keywords = Keywords(Arg(2)) });
                break;
            case "73": case "edit":
                var edited = Find(Number(0));
                edited.Summary = Arg(1).Trim(); edited.Time = Arg(2).Trim(); edited.Keywords = Keywords(Arg(3));
                break;
            case "74": case "delete":
                var deleted = Find(Number(0));
                state.Memories.Remove(deleted); state.MemoryProposals.Remove(deleted); break;
            case "75": case "pin":
                var pinned = Find(Number(0));
                if (state.MemoryProposals.Contains(pinned)) throw new ArgumentException("请先采用记忆建议，再设置置顶。");
                if (!pinned.Pinned && state.Memories.Count(m => m.Pinned) >= InventoryState.MaxPinnedMemories)
                    throw new ArgumentException("最多置顶4条记忆，请先取消其他置顶。");
                pinned.Pinned = !pinned.Pinned; break;
            case "76": case "active":
                var active = Find(Number(0));
                if (state.MemoryProposals.Contains(active)) throw new ArgumentException("待确认建议不能切换生效状态。");
                active.Active = !active.Active; break;
            case "77": case "accept":
                var accepted = state.MemoryProposals.FirstOrDefault(m => m.Id == Number(0)) ?? throw new ArgumentException("指定记忆建议不存在。");
                if (state.Memories.Count >= InventoryState.MaxMemories) throw new ArgumentException("历史记忆已满，请先清理旧条目。");
                if (Arg(1).Length > 0) accepted.Summary = Arg(1).Trim();
                state.MemoryProposals.Remove(accepted); state.Memories.Add(accepted); break;
            case "78": case "reject":
                var rejected = state.MemoryProposals.FirstOrDefault(m => m.Id == Number(0)) ?? throw new ArgumentException("指定记忆建议不存在。");
                state.MemoryProposals.Remove(rejected); break;
            default: throw new ArgumentException("不支持的历史记忆操作。");
        }
        if (mutated) state.Revision = checked(state.Revision + 1);
        result[0] = state.Snapshot();
        return result;
    }
}
