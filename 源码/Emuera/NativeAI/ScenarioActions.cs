#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;

namespace MinorShift.Emuera.NativeAI;

internal sealed record ScenarioActionAvailability(bool CanInput, bool CanSleep, bool CanHunt, bool CanRest,
    string InputReason, string SleepReason, string HuntReason, string RestReason)
{
    internal string[] Fields() => [CanInput ? "1" : "0", CanSleep ? "1" : "0", CanHunt ? "1" : "0", CanRest ? "1" : "0",
        InputReason, SleepReason, HuntReason, RestReason];
}

internal sealed record LocalScenarioAction(string Snapshot, string Narrative, string Changes = "", int TimeCost = 0);

// This class performs no I/O, API calls, random rolls or clock writes. The ERB layer only
// advances time after receiving a fully validated local result. Availability and execution
// use the same scenario rules so a stale UI or alternate entry cannot bypass location checks.
internal static class ScenarioActions
{
    internal static ScenarioActionAvailability Availability(InventoryState state, ScenarioDefinition scenario,
        int day, int time, int lastHuntDay)
    {
        int ticks = scenario.Parameter("ticks_per_day"), huntTicks = scenario.Parameter("hunt_ticks");
        if (day < 1 || ticks is < 1 or > 1000 || time < 0 || time >= ticks || huntTicks is < 1 || huntTicks > ticks)
            throw new ArgumentException("当前日期、时间或行动耗时无效。");
        string sleep = LocationReason(state, scenario, "sleep");
        string hunt = LocationReason(state, scenario, "hunt");
        if (hunt.Length == 0 && lastHuntDay == day) hunt = "今天已经完成过狩猎。";
        if (hunt.Length == 0 && time + huntTicks + state.MovementRemainder > ticks) hunt = $"狩猎需要{huntTicks}格，今天剩余时间不足。";
        string rest = LocationReason(state, scenario, "rest");
        return new(true, sleep.Length == 0, hunt.Length == 0, rest.Length == 0, "", sleep, hunt, rest);
    }

    internal static void Ensure(InventoryState state, ScenarioDefinition scenario, string action,
        int day, int time, int lastHuntDay)
    {
        // Movement and trade do not depend on whether this scenario implements hunting.
        if (action is "move" or "trade")
        {
            int ticks = scenario.Parameter("ticks_per_day");
            if (day < 1 || ticks is < 1 or > 1000 || time < 0 || time >= ticks)
                throw new ArgumentException("当前日期、时间或行动耗时无效。");
            string localReason = action == "move" ? MoveReason(state, scenario, time) : LocationReason(state, scenario, action);
            if (localReason.Length > 0) throw new InvalidOperationException(localReason);
            return;
        }
        var status = Availability(state, scenario, day, time, lastHuntDay);
        string reason = action switch
        {
            "input" => status.InputReason, "sleep" => status.SleepReason,
            "hunt" => status.HuntReason, "rest" => status.RestReason,
            _ => throw new ArgumentException("未知的常用行动。"),
        };
        if (reason.Length > 0) throw new InvalidOperationException(reason);
    }

    internal static void EnsureLocation(InventoryState state, ScenarioDefinition scenario, string action)
    {
        string reason = LocationReason(state, scenario, action);
        if (reason.Length > 0) throw new InvalidOperationException(reason);
    }

    internal static string LocationReason(InventoryState state, ScenarioDefinition scenario, string action)
    {
        if (action is not ("hunt" or "sleep" or "rest" or "move" or "trade")) throw new ArgumentException("未知的常用行动。");
        if (state.PlayerPosition?.IsWilderness == true && action is "rest" or "move") return "";
        string policy = PolicyReason(state, scenario, action);
        if (policy.Length > 0) return policy;
        if (action == "move" && !scenario.Parameters.ContainsKey("meters_per_action_point") && KnownMovementTargets(state).Count == 0) return "当前没有已知的相邻通路。";
        return "";
    }

    private static string PolicyReason(InventoryState state, ScenarioDefinition scenario, string action)
    {
        // Older packages without an explicit policy keep their own ERB rules. Once a package
        // declares one, an unknown/new location cannot silently become an allowed hunting area.
        if (!scenario.ActionLocations.TryGetValue(action, out var allowed)) return action == "trade" ? "当前没有可进行交易的地点。" : "";
        string name = action switch { "hunt" => "狩猎", "sleep" => "入眠", "rest" => "休息", "move" => "移动", _ => "交易" };
        string? current = state.Scene?.Location;
        if (current == null) return "当前地点尚未确认，暂不能" + name + "。";
        var template = scenario.World.Scenes.FirstOrDefault(card => card.Name == current || card.Aliases.Contains(current));
        if (template != null && allowed.Contains(template.Id)) return "";
        string locations = string.Join("、", scenario.World.Scenes.Where(card => allowed.Contains(card.Id)).Select(card => card.Name));
        return locations.Length == 0 ? "当前没有可进行" + name + "的地点。" : "当前地点不能" + name + "；可在" + locations + "进行。";
    }

    internal static SceneCard? CurrentCard(InventoryState state) => state.KnownScenes.FirstOrDefault(card =>
        card.Name == state.Scene?.Location || card.Aliases.Contains(state.Scene?.Location ?? ""));
    private static IReadOnlyList<SceneCard> KnownMovementTargets(InventoryState state)
    {
        var current = CurrentCard(state);
        return current == null ? [] : state.KnownScenes.Where(card => current.Adjacent.Contains(card.Id)).OrderBy(card => card.Id).ToArray();
    }
    internal static IReadOnlyList<SceneCard> MovementTargets(InventoryState state, ScenarioDefinition scenario) =>
        PolicyReason(state, scenario, "move").Length > 0 ? [] : KnownMovementTargets(state);
    private static int MoveCost(ScenarioDefinition scenario) => scenario.Parameters.GetValueOrDefault("move_ticks", 1);
    private static string MoveReason(InventoryState state, ScenarioDefinition scenario, int time)
    {
        string reason = LocationReason(state, scenario, "move"); if (reason.Length > 0) return reason;
        if (scenario.Parameters.ContainsKey("meters_per_action_point"))
            return scenario.Parameter("ticks_per_day") - time - state.MovementRemainder > 0 ? "" : "今天已没有剩余行动点。";
        int cost = MoveCost(scenario);
        if (cost is < 1 or > 1000) throw new ArgumentException("移动耗时无效。");
        return time + cost > scenario.Parameter("ticks_per_day") ? "今天剩余时间不足，暂时不能移动。" : "";
    }

    internal static LocalScenarioAction ApplyMove(InventoryState source, ScenarioDefinition scenario, int destinationId)
    {
        EnsureLocation(source, scenario, "move");
        var target = MovementTargets(source, scenario).FirstOrDefault(card => card.Id == destinationId)
            ?? throw new InvalidOperationException("只能前往已知的相邻地点。");
        if (!source.Characters.ContainsKey("player")) throw new InvalidOperationException("主角尚未登记。");
        int cost = MoveCost(scenario); if (cost is < 1 or > 1000) throw new ArgumentException("移动耗时无效。");
        var candidate = InventoryState.Read(source.Snapshot());
        string previous = candidate.Scene?.Location ?? "原地";
        // Only the player's movement is authorized by this fixed action. People already
        // at the destination become present; those left behind retain their locations.
        var present = candidate.Characters.Keys.Where(id => id == "player" ||
            candidate.CharacterLocations.TryGetValue(id, out var location) && (location == target.Name || target.Aliases.Contains(location))).ToList();
        candidate.SetScene(new SceneState { Location = target.Name, PresentCharacters = present, Summary = "主角沿路抵达" + target.Name + "。" });
        candidate.Revision = checked(candidate.Revision + 1); candidate.LastTurnId = Guid.NewGuid().ToString("N");
        string others = string.Join("、", present.Where(id => id != "player").Select(id => candidate.Characters[id]));
        string narrative = "你离开" + previous + "，沿路前往" + target.Name + "。" + (others.Length > 0 ? "抵达时，" + others + "正在这里。" : "你抵达了目的地。");
        return new(candidate.Snapshot(), narrative, "主角所在地：" + previous + " → " + target.Name, cost);
    }

    internal static string[] MenuFields(InventoryState state, ScenarioDefinition scenario, int day, int time, int lastHuntDay)
    {
        var availability = Availability(state, scenario, day, time, lastHuntDay);
        var result = new string[17]; Array.Copy(availability.Fields(), result, 8);
        result[8] = RecoveryHint(state, scenario, "sleep"); result[9] = RecoveryHint(state, scenario, "rest");
        result[10] = result[11] = result[12] = result[16] = ""; result[15] = "0";
        var exclusive = Exclusive(state, scenario);
        if (exclusive is { } entry)
        {
            result[10] = entry.Key; result[11] = entry.Value.Label; result[12] = entry.Value.Hint;
            string reason = entry.Key == "hunt" ? availability.HuntReason : LocationReason(state, scenario, entry.Key);
            result[15] = reason.Length == 0 ? "1" : "0"; result[16] = reason;
        }
        string move = MoveReason(state, scenario, time); result[13] = move.Length == 0 ? "1" : "0"; result[14] = move;
        return result;
    }

    internal static KeyValuePair<string, ScenarioLocationAction>? Exclusive(InventoryState state, ScenarioDefinition scenario)
    {
        foreach (var entry in scenario.LocationActions)
            if (LocationReason(state, scenario, entry.Key).Length == 0) return entry;
        if (scenario.LocationActions.Count == 0 && scenario.ActionLocations.ContainsKey("hunt") && LocationReason(state, scenario, "hunt").Length == 0)
            return new("hunt", new() { Label = "狩猎", Hint = "掷骰决定本次收获" });
        return null;
    }

    private static int RecoveryBonus(InventoryState state, ScenarioDefinition scenario, ScenarioRecoveryRule rule)
    {
        string? location = state.Scene?.Location;
        var template = scenario.World.Scenes.FirstOrDefault(card => card.Name == location || card.Aliases.Contains(location ?? ""));
        return template == null ? 0 : rule.LocationBonus.GetValueOrDefault(template.Id);
    }
    internal static string RecoveryHint(InventoryState state, ScenarioDefinition scenario, string action)
    {
        if (scenario.FatigueRules.Enabled)
        {
            decimal points = FatigueSystem.RecoveryAmount(state, scenario, action);
            decimal basic = action == "sleep" ? scenario.FatigueRules.SleepRecovery : scenario.FatigueRules.RestRecovery;
            return $"疲劳恢复 {points:0.###} 点" + (points > basic ? $"（环境加成 +{points - basic:0.###} 点）" : "");
        }
        if (!scenario.RecoveryRules.TryGetValue(action, out var rule)) return "";
        int bonus = RecoveryBonus(state, scenario, rule);
        return rule.Label + " " + (rule.Levels + bonus) + " 级" + (bonus > 0 ? "（环境加成 +" + bonus + " 级）" : "");
    }

    private static string Recover(InventoryState candidate, ScenarioDefinition scenario, string action)
    {
        var changes = new List<string>();
        if (scenario.FatigueRules.Enabled)
        {
            string pointChange = FatigueSystem.Change(candidate, scenario, -FatigueSystem.RecoveryAmount(candidate, scenario, action), "休息恢复", "休息后");
            if (pointChange.Length > 0) changes.Add(pointChange);
        }
        if (!scenario.RecoveryRules.TryGetValue(action, out var rule)) return string.Join("\n", changes);
        int levels = rule.Levels + RecoveryBonus(candidate, scenario, rule); if (levels <= 0) return string.Join("\n", changes);
        foreach (var status in candidate.Statuses.Where(status => status.Character == "player" &&
            // Numeric fatigue has already recovered once. Only existing legacy energy
            // records retain degree-based compatibility; NPCs and injuries are untouched.
            (!scenario.FatigueRules.Enabled || status.Category.Trim() == "精力") &&
            rule.Categories.Contains(status.Category.Trim(), StringComparer.Ordinal) &&
            status.Category.Trim() is not ("伤势" or "疾病" or "伤病")).ToArray())
        {
            // Aliases map to the same numeric severity, while an unknown label remains
            // untouched. Trimming also repairs harmless whitespace in older AI saves.
            if (!rule.SeverityLevels.TryGetValue(status.Severity.Trim(), out int current)) continue;
            int after = current - levels;
            if (after <= 0) { candidate.Statuses.Remove(status); candidate.FatigueHistory.Remove(status.Id); changes.Add("主角恢复：" + status.Name + "已消退"); }
            else
            {
                string previous = status.Severity; status.Severity = rule.SeverityLabels[after - 1];
                status.Description = "休息后，" + status.Name + "有所缓解，尚未完全恢复。";
                status.Effects = status.Name + "仍会影响持续活动，需要继续休息。"; status.UpdatedAt = "休息后";
                changes.Add("主角：" + status.Name + " " + previous + " → " + status.Severity);
            }
        }
        return string.Join("\n", changes);
    }

    internal static LocalScenarioAction CompleteLocal(InventoryState source, ScenarioDefinition scenario, string action)
    {
        if (action is not ("sleep" or "rest")) throw new ArgumentException("此行动不是本地休息行动。");
        EnsureLocation(source, scenario, action);
        var candidate = InventoryState.Read(source.Snapshot());
        if (action == "sleep") candidate.MovementRemainder = 0;
        bool bonus = scenario.FatigueRules.Enabled
            ? FatigueSystem.RecoveryAmount(candidate, scenario, action) > (action == "sleep" ? scenario.FatigueRules.SleepRecovery : scenario.FatigueRules.RestRecovery)
            : scenario.RecoveryRules.TryGetValue(action, out var recovery) && RecoveryBonus(candidate, scenario, recovery) > 0;
        var text = LocalActionNarration.Build(candidate, action == "sleep", bonus);
        string changes = Recover(candidate, scenario, action);
        if (candidate.Scene != null) candidate.Scene.Summary = text.Summary;
        candidate.Revision = checked(candidate.Revision + 1);
        candidate.LastTurnId = Guid.NewGuid().ToString("N");
        return new(candidate.Snapshot(), text.Narrative, changes);
    }
}
