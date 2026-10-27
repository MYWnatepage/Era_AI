#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text.Json;
using MinorShift.Emuera.NativeAI;

namespace MinorShift.Emuera.NativeDisplay;

internal sealed class TravelWaypoint
{
    public float X { get; set; }
    public float Z { get; set; }
}
internal sealed class TravelRouteRequest
{
    public int BaseRevision { get; set; }
    public List<TravelWaypoint> Points { get; set; } = [];
}
internal sealed record TravelQuote(bool Allowed, string Text);

// Re-measure the ground route locally. No distance/cost supplied by an AI or UI is trusted.
internal sealed class NativeMapTravel
{
    private readonly InventoryState state;
    private readonly ScenarioDefinition scenario;
    private readonly NativeSceneWorld world;
    private readonly NativeMapGround ground;
    private readonly int day, time;
    internal NativeMapTravel(InventoryState state, ScenarioDefinition scenario, NativeSceneWorld world, int day, int time)
    { this.state = state; this.scenario = scenario; this.world = world; ground = new(world); this.day = day; this.time = time; }
    internal string Instruction => $"按住右键画线 · 每 {scenario.Parameter("meters_per_action_point")} 米消耗 1 行动点 · 剩余 {scenario.Parameter("ticks_per_day") - time - state.MovementRemainder:0.###}";

    internal string Request(IReadOnlyList<Vector3> points) => JsonSerializer.Serialize(new TravelRouteRequest
    { BaseRevision = state.Revision, Points = points.Select(p => new TravelWaypoint { X = p.X, Z = p.Z }).ToList() }, InventoryState.Json);

    private (double Distance, decimal Cost, Vector3 End) Measure(TravelRouteRequest request)
    {
        ScenarioActions.Ensure(state, scenario, "move", day, time, 0);
        int metres = scenario.Parameter("meters_per_action_point");
        if (metres is < 1 or > 1000000) throw new ArgumentException("移动距离规则无效。");
        if (request.BaseRevision != state.Revision || request.Points == null || request.Points.Count is < 2 or > 4096)
            throw new InvalidOperationException("路线已过期或尚未绘制完成，请重新画线。");
        var origin = world.CurrentMarker ?? throw new InvalidOperationException("当前位置尚未确认。");
        var first = request.Points[0];
        if (first == null || Vector2.Distance(new(first.X, first.Z), new(origin.X, origin.Z)) > .01f)
            throw new InvalidOperationException("路线必须从当前位置开始。");
        Vector3 previous = default, end = default; double distance = 0;
        for (int i = 0; i < request.Points.Count; i++)
        {
            var point = request.Points[i];
            if (point == null || !ground.TryPoint(point.X, point.Z, out end)) throw new ArgumentException("路线超出地图地面。");
            if (i > 0)
            {
                if (Vector2.Distance(new(previous.X, previous.Z), new(end.X, end.Z)) > .51f)
                    throw new ArgumentException("路线采样不连续，请重新画线。");
                distance += Vector3.Distance(previous, end) * world.MetersPerUnit;
            }
            previous = end;
        }
        if (distance < .1 || !double.IsFinite(distance)) throw new ArgumentException("请先画出一条有效路线。");
        decimal cost = decimal.Round((decimal)distance / metres, 9, MidpointRounding.AwayFromZero);
        return (distance, cost, end);
    }

    internal TravelQuote Quote(IReadOnlyList<Vector3> points)
    {
        try
        {
            var request = new TravelRouteRequest { BaseRevision = state.Revision,
                Points = points.Select(p => new TravelWaypoint { X = p.X, Z = p.Z }).ToList() };
            var measured = Measure(request);
            decimal available = scenario.Parameter("ticks_per_day") - time - state.MovementRemainder;
            bool allowed = measured.Cost <= available;
            decimal fatigue = Math.Min(100 - FatigueSystem.Points(state), FatigueSystem.MovementCost(measured.Distance, scenario));
            return new(allowed, $"距离 {measured.Distance:0.#} 米 · 消耗 {measured.Cost:0.###} 行动点 · 剩余 {available:0.###}" +
                (scenario.FatigueRules.Enabled ? $" · 疲劳 +{fatigue:0.###}" : "") +
                (allowed ? "" : " · 行动点不足，请缩短路线"));
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException)
        { return new(false, error.Message); }
    }

    internal LocalScenarioAction Apply(string json)
    {
        if (json.Length > 500000) throw new ArgumentException("路线过长。");
        using (var document = JsonDocument.Parse(json)) InventoryReply.RejectDuplicateProperties(document.RootElement);
        var request = JsonSerializer.Deserialize<TravelRouteRequest>(json, InventoryState.Json) ?? throw new ArgumentException("路线无效。");
        var measured = Measure(request);
        decimal available = scenario.Parameter("ticks_per_day") - time - state.MovementRemainder;
        if (measured.Cost > available) throw new InvalidOperationException("今天的行动点不足，请缩短路线。");
        var end = measured.End;
        var site = SiteAt(end.X, end.Z);
        string location = site?.Name ?? "野外";
        var candidate = InventoryState.Read(state.Snapshot());
        candidate.PlayerPosition = new() { X = end.X, Z = end.Z, Location = location, IsWilderness = site == null };
        var present = candidate.Characters.Keys.Where(id => id == "player" || site != null &&
            candidate.CharacterLocations.GetValueOrDefault(id) == location).ToList();
        candidate.SetScene(new() { Location = location, PresentCharacters = present, Summary = SceneNarration.Arrival(location) });
        decimal total = candidate.MovementRemainder + measured.Cost;
        int ticks = (int)decimal.Floor(total); candidate.MovementRemainder = total - ticks;
        string fatigueChange = FatigueSystem.Change(candidate, scenario, FatigueSystem.MovementCost(measured.Distance, scenario), "行走", "本次行走后");
        candidate.Revision = checked(candidate.Revision + 1); candidate.LastTurnId = Guid.NewGuid().ToString("N");
        string narrative = SceneNarration.Arrival(location);
        string changes = $"移动消耗 {measured.Cost:0.###} 行动点；剩余 {available - measured.Cost:0.###} 行动点。\n当前地点：{location}";
        if (fatigueChange.Length > 0) changes += "\n" + fatigueChange;
        return new(candidate.Snapshot(), narrative, changes, ticks);
    }

    private NativeSceneSite? SiteAt(float x, float z)
    {
        // Fixed regions and authored river/road strips identify local capabilities;
        // everything else remains wilderness rather than snapping to a nearby building.
        foreach (var site in world.Sites)
            if (Inside(site.Outline, x, z)) return site;
        foreach (var mesh in new[] { world.River, world.Routes })
            for (int i = 0; i + 29 < mesh.Length; i += 30)
                if (Inside([new(mesh[i], mesh[i + 2]), new(mesh[i + 10], mesh[i + 12]), new(mesh[i + 20], mesh[i + 22])], x, z))
                {
                    int owner = (int)mesh[i + 6];
                    var site = world.Sites.FirstOrDefault(s => s.RenderId == owner);
                    if (site != null) return site;
                }
        return null;
    }
    private static bool Inside(Vector2[] polygon, float x, float z)
    {
        bool inside = false;
        for (int i = 0, j = polygon.Length - 1; i < polygon.Length; j = i++)
        {
            var a = polygon[i]; var b = polygon[j];
            if ((a.Y > z) != (b.Y > z) && x < (b.X - a.X) * (z - a.Y) / (b.Y - a.Y) + a.X) inside = !inside;
        }
        return inside;
    }
}
