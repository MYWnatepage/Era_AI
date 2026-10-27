#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;

namespace MinorShift.Emuera.NativeAI;

internal sealed partial class InventoryState
{
    // Only legacy duplicate fatigue needs an archive. It stays in the same save and
    // never becomes ordinary prompt context or a growing per-turn history.
    public Dictionary<int, List<CharacterStatus>> FatigueHistory { get; set; } = new();

    internal void NormalizeFatigueStatuses()
    {
        foreach (int id in FatigueHistory.Keys.Where(id => !Statuses.Any(s => s.Id == id)).ToArray()) FatigueHistory.Remove(id);
        foreach (var group in Statuses.Where(s => s.Category.Trim() == "疲劳").GroupBy(s => s.Character).ToArray())
        {
            var records = group.OrderBy(s => s.Id).ToArray();
            var kept = records[0];
            if (records.Length > 1)
            {
                // Unknown severity cannot safely be treated as less severe or healed.
                var representative = records.FirstOrDefault(s => FatigueLevel(s.Severity) == 0) ??
                    records.OrderByDescending(s => FatigueLevel(s.Severity)).First();
                string description = MergeFatigueText(records, s => s.Description);
                string effects = MergeFatigueText(records.Where(s => s.Effects.Length > 0), s => s.Effects);
                var archive = records.SelectMany(s => FatigueHistory.GetValueOrDefault(s.Id) ?? []).ToList();
                if (description.Length > 1000 || effects.Length > 500)
                {
                    // Keep every source verbatim locally; the active condition remains
                    // compact and its details page can show the earlier causes below it.
                    archive.AddRange(records.Select(CopyFatigueSource));
                    description = representative.Description;
                    effects = representative.Effects;
                }
                if (archive.Count > 0) FatigueHistory[kept.Id] = archive.DistinctBy(s =>
                    (s.Id, s.Name, s.Severity, s.Description, s.Effects, s.CreatedAt, s.UpdatedAt)).ToList();
                kept.Severity = representative.Severity.Trim();
                kept.Description = description; kept.Effects = effects;
                // Preserve the first condition's identity and timestamps; a migration
                // is not a new in-game event and does not invent an update time.
                foreach (var removed in records.Skip(1)) { Statuses.Remove(removed); FatigueHistory.Remove(removed.Id); }
            }
            kept.Name = "疲劳"; kept.Category = "疲劳";
        }
        // Numeric saves cannot acquire a second, independently edited player degree.
        if (FatiguePoints.HasValue) FatigueSystem.SyncStatus(this);
    }

    internal string FatigueDetail(CharacterStatus status)
    {
        if (!FatigueHistory.TryGetValue(status.Id, out var records)) return status.Description;
        return status.Description + "\n\n此前的疲劳情况：\n" + string.Join("\n", records.Select(s =>
            s.Name + " / " + s.Severity + "：" + s.Description + (s.Effects.Length > 0 ? "\n影响：" + s.Effects : "")));
    }

    private void ValidateFatigueHistory()
    {
        if (FatigueHistory == null || FatigueHistory.Count > 128 || FatigueHistory.Values.Any(v => v == null) ||
            FatigueHistory.Values.Sum(v => v.Count) > 256) throw new ArgumentException("旧疲劳资料档案无效。");
        foreach (var (id, records) in FatigueHistory)
        {
            if (id < 1 || id >= NextStatusId || records.Count == 0) throw new ArgumentException("旧疲劳资料编号无效。");
            foreach (var s in records)
            {
                if (s == null || s.Id < 1 || s.Id >= NextStatusId || !Characters.ContainsKey(s.Character) || s.Category == null || s.Category.Trim() != "疲劳" ||
                    Statuses.FirstOrDefault(entry => entry.Id == id) is { } active && active.Character != s.Character)
                    throw new ArgumentException("旧疲劳资料内容无效。");
                Text(s.Name, 80, true); Text(s.Category, 40, true); Text(s.Severity, 40, true);
                Text(s.Description, 1000, true); Text(s.Effects, 500, false); Text(s.CreatedAt, 160, false); Text(s.UpdatedAt, 160, false);
            }
        }
    }

    private static int FatigueLevel(string severity) => severity.Trim() switch
    { "轻微" or "轻度" => 1, "中度" or "中等" => 2, "重度" or "严重" => 3, _ => 0 };
    private static string MergeFatigueText(IEnumerable<CharacterStatus> records, Func<CharacterStatus, string> value) =>
        string.Join("\n", records.Select(s => s.Name + "：" + value(s)).Distinct(StringComparer.Ordinal));
    private static CharacterStatus CopyFatigueSource(CharacterStatus s) => new()
    { Id = s.Id, Character = s.Character, Name = s.Name, Category = s.Category, Severity = s.Severity,
        Description = s.Description, Effects = s.Effects, CreatedAt = s.CreatedAt, UpdatedAt = s.UpdatedAt };
}
