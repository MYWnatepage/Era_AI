#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;

namespace MinorShift.Emuera.NativeAI;

internal sealed class FatigueRules
{
    public bool Enabled { get; set; }
    public decimal InitialPoints { get; set; }
    public decimal PointsPer500Metres { get; set; } = 5;
    public decimal HuntCost { get; set; } = 25;
    public decimal RestRecovery { get; set; } = 15;
    public decimal SleepRecovery { get; set; } = 60;
    public Dictionary<int, decimal> RestLocationBonus { get; set; } = new();
    public Dictionary<int, decimal> SleepLocationBonus { get; set; } = new();
    public decimal LightEffortCost { get; set; } = 5;
    public decimal ModerateEffortCost { get; set; } = 10;
    public decimal HeavyEffortCost { get; set; } = 20;
    public int[] DicePenalties { get; set; } = [0, 2, 4, 6];

    internal void Validate(SceneCatalog world)
    {
        decimal[] amounts = [InitialPoints, PointsPer500Metres, HuntCost, RestRecovery, SleepRecovery,
            LightEffortCost, ModerateEffortCost, HeavyEffortCost];
        if (amounts.Any(value => value is < 0 or > 100) || RestLocationBonus == null || SleepLocationBonus == null ||
            DicePenalties == null || DicePenalties.Length != 4 || DicePenalties[0] != 0 ||
            DicePenalties.Any(value => value is < 0 or > 19) || !DicePenalties.SequenceEqual(DicePenalties.OrderBy(value => value)) ||
            LightEffortCost > ModerateEffortCost || ModerateEffortCost > HeavyEffortCost)
            throw new ArgumentException("疲劳点与骰点减值规则无效。");
        void ValidateBonus(Dictionary<int, decimal> bonuses, decimal basic)
        {
            if (bonuses.Count > 96 || bonuses.Any(pair => pair.Value < 0 || pair.Value + basic > 100 || !world.Scenes.Any(scene => scene.Id == pair.Key)))
                throw new ArgumentException("疲劳恢复的地点加成无效。");
        }
        ValidateBonus(RestLocationBonus, RestRecovery); ValidateBonus(SleepLocationBonus, SleepRecovery);
    }
}

internal sealed partial class InventoryState
{
    // Null is a legacy or non-numeric scenario. Once enabled, this is the only source
    // of player fatigue severity. The condition row is a derived readable view.
    public decimal? FatiguePoints { get; set; }

    private void ValidateFatiguePoints()
    {
        if (FatiguePoints is < 0 or > 100) throw new ArgumentException("疲劳点必须在0至100之间。");
    }

    internal void BindFatigue(ScenarioDefinition scenario)
    {
        if (!scenario.FatigueRules.Enabled) return;
        if (!Characters.ContainsKey("player")) throw new ArgumentException("疲劳点需要已登记的主角。");
        if (!FatiguePoints.HasValue)
        {
            var old = Statuses.Where(status => status.Character == "player" && status.Category.Trim() == "疲劳").OrderBy(status => status.Id).ToArray();
            FatiguePoints = old.Length == 0 ? scenario.FatigueRules.InitialPoints : old.Max(status => status.Severity.Trim() switch
            { "轻微" or "轻度" => 25m, "中度" or "中等" => 50m, _ => 75m });
            // Existing records remain inspectable after their degree becomes a score.
            foreach (var source in old)
            {
                if (!FatigueHistory.TryGetValue(source.Id, out var records)) FatigueHistory[source.Id] = records = [];
                if (!records.Any(record => record.Id == source.Id && record.Description == source.Description && record.Effects == source.Effects))
                {
                    if (FatigueHistory.Values.Sum(entries => entries.Count) >= 256) throw new ArgumentException("旧疲劳资料过多，未覆盖原有说明。");
                    records.Add(new() { Id = source.Id, Character = source.Character, Name = source.Name, Category = source.Category,
                        Severity = source.Severity, Description = source.Description, Effects = source.Effects,
                        CreatedAt = source.CreatedAt, UpdatedAt = source.UpdatedAt });
                }
            }
        }
        ValidateFatiguePoints();
        FatigueSystem.SyncStatus(this);
    }
}

internal static class FatigueSystem
{
    internal static decimal Points(InventoryState state) => state.FatiguePoints ?? 0;
    internal static int Level(decimal points) => points >= 75 ? 3 : points >= 50 ? 2 : points >= 25 ? 1 : 0;
    internal static string Label(decimal points) => Level(points) switch
    { 0 => "精力尚好", 1 => "轻微疲劳", 2 => "中度疲劳", _ => "重度疲劳" };
    internal static int Penalty(InventoryState state) => state.FatiguePoints.HasValue ? Level(Points(state)) * 2 : 0;
    internal static int Penalty(InventoryState state, ScenarioDefinition scenario) => scenario.FatigueRules.Enabled && state.FatiguePoints.HasValue
        ? scenario.FatigueRules.DicePenalties[Level(Points(state))] : 0;
    internal static decimal MovementCost(double metres, ScenarioDefinition scenario)
    {
        if (!double.IsFinite(metres) || metres < 0) throw new ArgumentException("行走距离无效。");
        return scenario.FatigueRules.Enabled ? decimal.Round((decimal)metres * scenario.FatigueRules.PointsPer500Metres / 500m, 6) : 0;
    }
    internal static decimal HuntCost(ScenarioDefinition scenario) => scenario.FatigueRules.Enabled ? scenario.FatigueRules.HuntCost : 0;
    internal static decimal ActionCost(ScenarioDefinition scenario, string action) => action == "hunt" ? HuntCost(scenario) : 0;
    internal static decimal RecoveryAmount(InventoryState state, ScenarioDefinition scenario, string action)
    {
        if (action is not ("rest" or "sleep")) throw new ArgumentException("未知疲劳恢复方式。");
        if (!scenario.FatigueRules.Enabled) return 0;
        var rules = scenario.FatigueRules;
        var card = scenario.World.Scenes.FirstOrDefault(scene => scene.Name == state.Scene?.Location || scene.Aliases.Contains(state.Scene?.Location ?? ""));
        var bonus = action == "rest" ? rules.RestLocationBonus : rules.SleepLocationBonus;
        return (action == "rest" ? rules.RestRecovery : rules.SleepRecovery) + (card == null ? 0 : bonus.GetValueOrDefault(card.Id));
    }

    internal static string Change(InventoryState state, ScenarioDefinition scenario, decimal delta, string reason, string time = "")
    {
        if (!scenario.FatigueRules.Enabled) return "";
        state.BindFatigue(scenario);
        decimal previous = Points(state);
        state.FatiguePoints = Math.Clamp(previous + delta, 0, 100);
        SyncStatus(state, time);
        decimal actual = Points(state) - previous;
        return actual == 0 ? "" : $"疲劳 {(actual > 0 ? "+" : "")}{actual:0.###}；当前 {Points(state):0.###}/100（{Label(Points(state))}）" +
            (string.IsNullOrWhiteSpace(reason) ? "" : " · " + reason);
    }
    internal static string ApplyEffort(InventoryState state, ScenarioDefinition scenario, string effort, string time = "")
    {
        decimal delta = effort switch
        {
            "none" => 0, "light" => scenario.FatigueRules.LightEffortCost,
            "moderate" => scenario.FatigueRules.ModerateEffortCost, "heavy" => scenario.FatigueRules.HeavyEffortCost,
            _ => throw new ArgumentException("活动强度应为none、light、moderate或heavy。")
        };
        return Change(state, scenario, delta, "本次活动", time);
    }

    internal static void SyncStatus(InventoryState state, string time = "")
    {
        if (!state.FatiguePoints.HasValue || !state.Characters.ContainsKey("player")) return;
        int level = Level(Points(state));
        var fatigue = state.Statuses.Where(status => status.Character == "player" && status.Category.Trim() == "疲劳").OrderBy(status => status.Id).ToArray();
        if (level == 0)
        {
            foreach (var item in fatigue) { state.Statuses.Remove(item); state.FatigueHistory.Remove(item.Id); }
            return;
        }
        CharacterStatus status;
        if (fatigue.Length == 0)
        {
            if (state.Statuses.Count >= 128 || state.NextStatusId >= int.MaxValue) throw new ArgumentException("人物状态记录已达上限。");
            status = new() { Id = state.NextStatusId++, Character = "player", CreatedAt = time };
            state.Statuses.Add(status);
        }
        else
        {
            status = fatigue[0];
            foreach (var other in fatigue.Skip(1)) { state.Statuses.Remove(other); state.FatigueHistory.Remove(other.Id); }
        }
        status.Name = status.Category = "疲劳";
        status.Severity = level == 1 ? "轻微" : level == 2 ? "中度" : "重度";
        status.Description = level switch { 1 => "四肢有些酸乏，仍能继续活动。", 2 => "身体明显发沉，做费力的事需要更加小心。", _ => "你已经十分疲惫，动作和判断都受到影响，最好先找地方歇一歇。" };
        status.Effects = "费力时更容易出错，休息后可以缓解。";
        if (time.Length > 0) status.UpdatedAt = time;
    }
}
