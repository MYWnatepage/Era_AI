using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace MinorShift.Emuera.NativeAI;

internal sealed record ConversationTurn(string Time, string Player, string Reply);

internal sealed class ConversationStore(string directory)
{
    private readonly object gate = new();
    private string GetPath(string scenario)
    {
        if (!Regex.IsMatch(scenario, "^[a-zA-Z0-9_-]{1,64}$")) throw new ArgumentException("剧本标识无效。");
        return Path.Combine(directory, scenario + ".json");
    }
    public List<ConversationTurn> Read(string scenario)
    {
        lock (gate)
        {
            var path = GetPath(scenario);
            if (!File.Exists(path)) return [];
            try { return JsonSerializer.Deserialize<List<ConversationTurn>>(File.ReadAllText(path)) ?? []; }
            catch (JsonException) { throw new InvalidOperationException("剧本记忆文件损坏，请备份后在游戏中清空记忆。"); }
        }
    }
    public void Append(string scenario, ConversationTurn turn)
    {
        lock (gate)
        {
            var turns = Read(scenario);
            turns.Add(turn);
            Write(scenario, turns.TakeLast(256).ToList());
        }
    }
    private void Write(string scenario, List<ConversationTurn> turns)
    {
        var path = GetPath(scenario);
        Directory.CreateDirectory(directory);
        File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(turns, new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
        File.Move(path + ".tmp", path, true);
    }
    public string Snapshot(string scenario) => JsonSerializer.Serialize(Read(scenario));
    public void Restore(string scenario, string snapshot)
    {
        List<ConversationTurn> turns;
        try
        {
            turns = JsonSerializer.Deserialize<List<ConversationTurn>>(snapshot) ?? throw new JsonException();
            if (turns.Count > 256 || turns.Any(t => t == null || t.Time == null || t.Player == null || t.Reply == null)) throw new JsonException();
        }
        catch (JsonException) { throw new InvalidOperationException("存档中的 AI 历史无效，未覆盖现有历史。"); }
        lock (gate)
        {
            var path = GetPath(scenario);
            // Preserve the original independent history before enabling save-bound restoration.
            if (File.Exists(path) && !File.Exists(path + ".before-save-sync")) File.Copy(path, path + ".before-save-sync");
            Write(scenario, turns);
        }
    }
    public void Clear(string scenario) { lock (gate) Write(scenario, []); }
    public string Display(string scenario)
    {
        var turns = Read(scenario);
        return turns.Count == 0 ? "目前还没有长期记忆。" : string.Join("\n\n", turns.TakeLast(20).Select(t => $"{t.Time}\n你：{t.Player}\n艾拉：{t.Reply}"));
    }
    public List<ConversationTurn> Context(string scenario, int budget)
    {
        if (budget <= 0) return [];
        // Character budget is an estimate; actual tokenization depends on the model.
        var remaining = Math.Clamp(budget, 0, 8192) * 2;
        var selected = new List<ConversationTurn>();
        foreach (var turn in Read(scenario).AsEnumerable().Reverse())
        {
            var size = turn.Player.Length + turn.Reply.Length;
            if (size > remaining) break;
            selected.Add(turn);
            remaining -= size;
        }
        selected.Reverse();
        return selected;
    }
}
