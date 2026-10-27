#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text.Json;

namespace MinorShift.Emuera.NativeDisplay;

// Geometry is built from an already detached/validated host payload. No gameplay state is writable here.
internal sealed class NativeSceneSite
{
    internal int Id { get; init; }
    internal int LayoutId { get; init; }
    internal int RenderId { get; init; }
    internal string Name { get; init; } = "";
    internal string Description { get; init; } = "";
    internal string Kind { get; init; } = "place";
    internal string Symbol { get; init; } = "place";
    internal string[] Facilities { get; init; } = [];
    internal string[] Adjacent { get; init; } = [];
    internal string[] People { get; init; } = [];
    internal string[] Conditions { get; init; } = [];
    internal string[] Actions { get; init; } = [];
    internal string[] Benefits { get; init; } = [];
    internal float X { get; init; }
    internal float Y { get; set; }
    internal float Z { get; init; }
    internal float Radius { get; init; }
    internal float MarkerHeight { get; init; }
    internal Vector2[] Outline { get; init; } = [];
    internal bool HasLayout { get; init; }
}

internal sealed class NativeSceneWorld
{
    // Position3, normal3, compact render owner1, barycentric3. Edge: position3, owner1.
    internal float[] Terrain { get; private set; } = [];
    internal float[] Solids { get; private set; } = [];
    internal float[] River { get; private set; } = [];
    internal float[] Routes { get; private set; } = [];
    internal float[] Edges { get; private set; } = [];
    internal int TerrainEdgeFloatCount { get; private set; }
    internal IReadOnlyList<NativeSceneSite> Sites { get; private set; } = [];
    internal Dictionary<int, int> RenderIdToSiteId { get; } = new();
    internal int CurrentId { get; private set; }
    internal NativeSceneSite? CurrentMarker { get; private set; }
    internal Vector2 Origin { get; private set; }
    internal float MetersPerUnit { get; private set; } = 10;
    // X=minX, Y=maxX, Z=minZ, W=maxZ.
    internal Vector4 Bounds { get; private set; }
    internal Vector4 ExploredBounds { get; private set; }
    internal float HeightAt(float x, float z) => (float)Height(x, z);

    private readonly List<float> terrain = [], solids = [], edges = [], river = [], routes = [];
    private readonly List<Feature> riverFeatures = [], routeFeatures = [];
    private readonly Dictionary<int, int> ownerMap = new();
    private JsonElement layout, terrainSettings;
    private double minX = -110, maxX = 110, minZ = -110, maxZ = 110;

    private readonly record struct P2(double X, double Z);
    private readonly record struct P3(double X, double Y, double Z);
    private sealed class Feature(JsonElement data, int owner)
    {
        internal JsonElement Data = data;
        internal int Owner = owner;
        internal List<P2> Path = [];
        internal double? Width, Offset, ValleyDepth, ValleyWidth;
        internal bool HasCenterline => Get(Data, "centerline").ValueKind == JsonValueKind.Object;
    }

    internal static NativeSceneWorld Build(string payloadJson)
    {
        using var document = JsonDocument.Parse(payloadJson, new JsonDocumentOptions { MaxDepth = 64 });
        var world = new NativeSceneWorld();
        world.Generate(document.RootElement);
        return world;
    }

    private static JsonElement Get(JsonElement element, string name) => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) ? value : default;
    private static IEnumerable<JsonElement> List(JsonElement element) => element.ValueKind == JsonValueKind.Array ? element.EnumerateArray() : [];
    private static double Finite(JsonElement value, double fallback = 0) => value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number) && double.IsFinite(number) ? number : fallback;
    private static double Number(JsonElement element, string name, double fallback = 0) => Finite(Get(element, name), fallback);
    private static bool HasNumber(JsonElement element, string name) => Get(element, name).ValueKind == JsonValueKind.Number && double.IsFinite(Finite(Get(element, name), double.NaN));
    private static int PositiveId(JsonElement value) => value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number) && number > 0 ? number : 0;
    private static string Text(JsonElement element, string name, string fallback = "") => Get(element, name) is var value && value.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(value.GetString()) ? value.GetString()! : fallback;
    private static string[] Strings(JsonElement element, string name) => List(Get(element, name)).Where(v => v.ValueKind == JsonValueKind.String).Select(v => v.GetString()!).ToArray();
    private static double Clamp(double value, double low, double high) => Math.Max(low, Math.Min(high, value));
    private static double Noise(double a, double b = 0)
    {
        double value = Math.Sin(a * 127.1 + b * 311.7 + 31.481) * 43758.5453123;
        return value - Math.Floor(value);
    }
    private static double Gaussian(double x, double z, double cx, double cz, double sx, double sz) => Math.Exp(-(Math.Pow((x - cx) / sx, 2) + Math.Pow((z - cz) / sz, 2)));
    private static double Length(double x, double z) => Math.Sqrt(x * x + z * z);
    private static bool Is(string value, params string[] choices) => Array.IndexOf(choices, value) >= 0;
    private static List<P2> Polygon(JsonElement value) => List(value).Where(p => p.ValueKind == JsonValueKind.Array && p.GetArrayLength() >= 2 &&
        double.IsFinite(Finite(p[0], double.NaN)) && double.IsFinite(Finite(p[1], double.NaN))).Select(p => new P2(Finite(p[0]), Finite(p[1]))).ToList();

    private IEnumerable<Feature> Features(string name)
    {
        foreach (var feature in List(Get(layout, name)))
        {
            var ownerValue = Get(feature, "owner");
            if (ownerValue.ValueKind != JsonValueKind.Number || !ownerValue.TryGetInt32(out int owner)) continue;
            if (owner == 0) yield return new Feature(feature, 0);
            else if (ownerMap.TryGetValue(owner, out int renderId)) yield return new Feature(feature, renderId);
        }
    }

    private void Generate(JsonElement input)
    {
        var candidate = Get(input, "layout");
        if (Number(candidate, "format_version") == 1) layout = candidate.Clone();
        terrainSettings = Get(layout, "terrain");
        var bound = Get(layout, "bounds");
        minX = Number(bound, "minX", -110); maxX = Number(bound, "maxX", 110);
        minZ = Number(bound, "minZ", -110); maxZ = Number(bound, "maxZ", 110);
        if (maxX - minX < 16 || maxZ - minZ < 16) { minX = minZ = -110; maxX = maxZ = 110; }
        Bounds = new((float)minX, (float)maxX, (float)minZ, (float)maxZ);
        var definitions = new Dictionary<int, JsonElement>();
        foreach (var definition in List(Get(layout, "sites")))
            if (PositiveId(Get(definition, "id")) is int id && id > 0) definitions[id] = definition;
        var sites = new List<NativeSceneSite>();
        var seen = new HashSet<int>();
        foreach (var source in List(Get(input, "sites")))
        {
            int id = PositiveId(Get(source, "id"));
            if (id == 0 || !seen.Add(id)) continue;
            if (sites.Count == 192) break;
            int renderId = sites.Count + 1, layoutId = PositiveId(Get(source, "layoutId"));
            definitions.TryGetValue(layoutId, out var definition);
            var map = Get(source, "map");
            bool hasLayout = HasNumber(definition, "x") && HasNumber(definition, "z");
            double x, z;
            if (hasLayout)
            {
                x = Number(definition, "x"); z = Number(definition, "z");
                ownerMap.TryAdd(layoutId, renderId);
            }
            else if (HasNumber(map, "x") && HasNumber(map, "y"))
            {
                x = (Clamp(Number(map, "x"), 0, 1) - .5) * (maxX - minX) * .68 + (minX + maxX) / 2;
                z = (Clamp(Number(map, "y"), 0, 1) - .5) * (maxZ - minZ) * .68 + (minZ + maxZ) / 2;
            }
            else
            {
                double angle = Noise(id, 717) * Math.PI * 2, radius = 14 + Noise(id, 823) * 44;
                x = Math.Cos(angle) * radius; z = Math.Sin(angle) * radius;
            }
            string kind = Text(definition, "kind", Text(map, "symbol", Text(source, "kind", "place"))).ToLowerInvariant();
            string symbol = Text(map, "symbol", Text(definition, "symbol", kind)).ToLowerInvariant();
            double siteRadius = Clamp(Number(definition, "radius", kind == "forest" ? 12 : 7), 1, 80);
            var outline = Polygon(Get(definition, "outline"));
            if (outline.Count < 3) outline = Enumerable.Range(0, 8).Select(i => new P2(x + Math.Cos(i * Math.PI / 4) * siteRadius, z + Math.Sin(i * Math.PI / 4) * siteRadius)).ToList();
            sites.Add(new NativeSceneSite
            {
                Id = id, LayoutId = layoutId, RenderId = renderId, Name = Text(source, "name"), Description = Text(source, "description"),
                Facilities = Strings(source, "facilities"), Adjacent = Strings(source, "adjacent"), People = Strings(source, "people"), Conditions = Strings(source, "conditions"), Actions = Strings(source, "actions"), Benefits = Strings(source, "benefits"),
                X = (float)x, Z = (float)z, Radius = (float)siteRadius, Kind = kind, Symbol = symbol, HasLayout = hasLayout,
                Outline = outline.Select(p => new Vector2((float)p.X, (float)p.Z)).ToArray(),
                MarkerHeight = (float)Clamp(Number(definition, "marker_height", Is(kind, "cabin", "house", "building", "market", "village", "town") ? 5 : .4), .15, 40)
            });
            RenderIdToSiteId.Add(renderId, id);
        }
        Sites = sites;
        riverFeatures.AddRange(Features("rivers")); routeFeatures.AddRange(Features("routes"));
        var fallbackSites = sites.Where(s => !s.HasLayout).ToArray();
        foreach (var site in fallbackSites)
        {
            double x = site.X, z = site.Z;
            if (Is(site.Symbol, "stream", "river", "water")) riverFeatures.Add(new Feature(default, site.RenderId)
            {
                Path = [new(x - 2, z - 12), new(x + 1, z - 5), new(x - 1, z + 3), new(x + 2, z + 12)], Width = 1, Offset = .13, ValleyDepth = .65, ValleyWidth = 3
            });
            else if (Is(site.Symbol, "path", "road")) routeFeatures.Add(new Feature(default, site.RenderId)
            {
                Path = [new(x - 7, z + 4), new(x, z), new(x + 8, z - 5)], Width = .4, Offset = .16
            });
        }
        foreach (var feature in riverFeatures)
        {
            if (feature.HasCenterline)
            {
                double start = Number(feature.Data, "min_z", minZ), end = Number(feature.Data, "max_z", maxZ), step = Clamp(Number(feature.Data, "step", 1), .25, 10);
                for (double z = start; z <= end && feature.Path.Count < 4096; z += step) feature.Path.Add(new(CenterlineX(feature, z), z));
            }
            else feature.Path = SmoothPolyline(feature.Path.Count > 0 ? feature.Path : Polygon(Get(feature.Data, "points")));
        }
        int columns = (int)Clamp(Math.Floor(Number(terrainSettings, "columns", 62) + .5), 8, 100), rows = (int)Clamp(Math.Floor(Number(terrainSettings, "rows", 58) + .5), 8, 100);
        var grid = new P3[rows + 1, columns + 1];
        for (int j = 0; j <= rows; j++) for (int i = 0; i <= columns; i++)
        {
            bool border = i == 0 || j == 0 || i == columns || j == rows;
            double x = minX + i * (maxX - minX) / columns + (border ? 0 : (Noise(i, j) - .5) * 1.44);
            double z = minZ + j * (maxZ - minZ) / rows + (border ? 0 : (Noise(j + 91, i + 33) - .5) * 1.48);
            grid[j, i] = new(x, Height(x, z), z);
        }
        for (int j = 0; j < rows; j++) for (int i = 0; i < columns; i++)
        {
            var a = grid[j, i]; var b = grid[j, i + 1]; var c = grid[j + 1, i]; var d = grid[j + 1, i + 1];
            if (Noise(i + 17, j + 8) > .5) { TopTriangle(terrain, a, b, d); TopTriangle(terrain, a, d, c); Line(a, d); }
            else { TopTriangle(terrain, a, b, c); TopTriangle(terrain, b, d, c); Line(b, c); }
            Line(a, b); Line(a, c); if (j == rows - 1) Line(c, d); if (i == columns - 1) Line(b, d);
        }
        TerrainEdgeFloatCount = edges.Count;
        foreach (var feature in riverFeatures)
        {
            double Width(int i) => Math.Max(.1, (feature.Width ?? Number(feature.Data, "half_width", 1.5)) + List(Get(feature.Data, "width_waves")).Sum(w => Number(w, "amplitude") * Math.Sin(i * Number(w, "frequency"))));
            var banks = Strip(river, feature.Path, Width, feature.Owner, feature.Offset ?? Number(feature.Data, "offset", .13));
            for (int i = 1; i < banks.Count; i++) { Line(banks[i - 1].Left, banks[i].Left, feature.Owner); Line(banks[i - 1].Right, banks[i].Right, feature.Owner); }
        }
        foreach (var feature in routeFeatures)
        {
            feature.Path = SmoothPolyline(feature.Path.Count > 0 ? feature.Path : Polygon(Get(feature.Data, "points")));
            Strip(routes, feature.Path, _ => Math.Max(.05, feature.Width ?? Number(feature.Data, "half_width", .4)), feature.Owner, feature.Offset ?? Number(feature.Data, "offset", .16));
        }
        foreach (var f in Features("buildings")) Building(Transformed(Number(f.Data, "x"), Number(f.Data, "z"), Polygon(Get(f.Data, "footprint")), Number(f.Data, "angle")), Math.Max(.1, Number(f.Data, "height", 3)), f.Owner);
        foreach (var f in Features("rocks")) Rock(Number(f.Data, "x"), Number(f.Data, "z"), Math.Max(.1, Number(f.Data, "radius", 1.4)), Math.Max(.1, Number(f.Data, "height", 1)), f.Owner, Number(f.Data, "seed"));
        foreach (var f in Features("trees")) Tree(Number(f.Data, "x"), Number(f.Data, "z"), Math.Max(.1, Number(f.Data, "radius", 1.4)), Math.Max(.1, Number(f.Data, "height", 4.5)), Number(f.Data, "seed"), f.Owner);
        foreach (var site in fallbackSites)
        {
            double x = site.X, z = site.Z; int id = site.Id, owner = site.RenderId;
            if (Is(site.Symbol, "forest", "woods", "grove")) for (int i = 0; i < 7; i++)
            {
                double a = i * 2.399963, r = 2 + Math.Sqrt(i) * 2.1;
                Tree(x + Math.Cos(a) * r, z + Math.Sin(a) * r, 1.1 + Noise(id, i) * .7, 3.8 + Noise(i, id) * 2, i, owner);
            }
            else if (Is(site.Symbol, "cabin", "house", "building")) Building(Transformed(x, z, [new(-2.8, -2.4), new(1.8, -2.4), new(3, -.8), new(3, 2.5), new(-2.8, 2.5)], -.12), 3.2, owner);
            else if (Is(site.Symbol, "market", "village", "town"))
            {
                Building(Transformed(x - 3, z, [new(-3.2, -2.8), new(2, -2.8), new(2, 0), new(0, 0), new(0, 2), new(-3.2, 2)], .1), 3.6, owner);
                Building(Transformed(x + 4, z - 2, [new(-1.4, -2.8), new(1.4, -2.8), new(1.4, 2.8), new(-1.4, 2.8)], -.2), 2.6, owner);
            }
            else if (!Is(site.Symbol, "stream", "river", "water", "path", "road")) Rock(x, z, 1.5, 1.2, owner, id);
        }
        foreach (var site in sites) site.Y = HeightAt(site.X, site.Z);
        int originId = PositiveId(Get(layout, "origin_scene"));
        var originSite = originId > 0 ? sites.FirstOrDefault(s => s.LayoutId == originId) : null;
        Origin = originSite == null ? Vector2.Zero : new(originSite.X, originSite.Z);
        CurrentId = PositiveId(Get(input, "currentId"));
        CurrentMarker = sites.FirstOrDefault(site => site.Id == CurrentId);
        var playerPosition = Get(input, "playerPosition");
        if (HasNumber(playerPosition, "x") && HasNumber(playerPosition, "z"))
            CurrentMarker = new NativeSceneSite { Id = CurrentId, Name = Text(input, "currentLocation"),
                X = (float)Number(playerPosition, "x"), Z = (float)Number(playerPosition, "z"), MarkerHeight = .4f };
        MetersPerUnit = (float)Math.Max(.001, Number(layout, "meters_per_unit", 10));
        ExploredBounds = sites.Count == 0 ? Bounds : new Vector4(
            sites.Min(s => Math.Min(s.X - s.Radius, s.Outline.Min(p => p.X))) - 8,
            sites.Max(s => Math.Max(s.X + s.Radius, s.Outline.Max(p => p.X))) + 8,
            sites.Min(s => Math.Min(s.Z - s.Radius, s.Outline.Min(p => p.Y))) - 8,
            sites.Max(s => Math.Max(s.Z + s.Radius, s.Outline.Max(p => p.Y))) + 8);
        Terrain = terrain.ToArray(); Solids = solids.ToArray(); River = river.ToArray(); Routes = routes.ToArray(); Edges = edges.ToArray();
        terrain.Clear(); solids.Clear(); river.Clear(); routes.Clear(); edges.Clear();
        terrain.TrimExcess(); solids.TrimExcess(); river.TrimExcess(); routes.TrimExcess(); edges.TrimExcess();
    }

    private static double CenterlineX(Feature feature, double z)
    {
        var curve = Get(feature.Data, "centerline");
        return Number(curve, "offset") + Number(curve, "slope") * z + List(Get(curve, "waves")).Sum(w => Number(w, "amplitude") * Math.Sin((z + Number(w, "phase")) / Math.Max(.1, Math.Abs(Number(w, "period", 1)))));
    }
    private static List<P2> SmoothPolyline(List<P2> points)
    {
        if (points.Count < 2) return points;
        var result = new List<P2>();
        for (int i = 0; i < points.Count - 1; i++)
        {
            var p0 = points[Math.Max(0, i - 1)]; var p1 = points[i]; var p2 = points[i + 1]; var p3 = points[Math.Min(points.Count - 1, i + 2)];
            int steps = Math.Max(5, Math.Min(512, (int)Math.Ceiling(Length(p2.X - p1.X, p2.Z - p1.Z))));
            for (int k = 0; k < steps; k++)
            {
                double t = (double)k / steps, t2 = t * t, t3 = t2 * t;
                double Axis(double a, double b, double c, double d) => .5 * (2 * b + (-a + c) * t + (2 * a - 5 * b + 4 * c - d) * t2 + (-a + 3 * b - 3 * c + d) * t3);
                result.Add(new(Axis(p0.X, p1.X, p2.X, p3.X), Axis(p0.Z, p1.Z, p2.Z, p3.Z)));
            }
        }
        result.Add(points[^1]); return result;
    }
    private static double DistanceToPath(double x, double z, List<P2> points)
    {
        double distance = double.PositiveInfinity;
        for (int i = 1; i < points.Count; i++)
        {
            var a = points[i - 1]; var b = points[i]; double dx = b.X - a.X, dz = b.Z - a.Z, length = dx * dx + dz * dz;
            double t = length != 0 ? Clamp(((x - a.X) * dx + (z - a.Z) * dz) / length, 0, 1) : 0;
            distance = Math.Min(distance, Length(x - a.X - t * dx, z - a.Z - t * dz));
        }
        return distance;
    }
    private double Height(double x, double z)
    {
        if (!double.IsFinite(x) || !double.IsFinite(z)) return 0;
        double height = Number(terrainSettings, "base_height", 2.9) + (z - Number(terrainSettings, "slope_origin_z")) * Number(terrainSettings, "slope_z");
        foreach (var hill in List(Get(terrainSettings, "hills"))) height += Number(hill, "height") * Gaussian(x, z, Number(hill, "x"), Number(hill, "z"), Math.Max(.1, Math.Abs(Number(hill, "width_x", 20))), Math.Max(.1, Math.Abs(Number(hill, "width_z", 20))));
        foreach (var ridge in List(Get(terrainSettings, "ridges")))
        {
            double ridgeLine = Number(ridge, "x") + Number(ridge, "wave_amplitude") * Math.Sin(z / Math.Max(.1, Math.Abs(Number(ridge, "wave_period", 20)))) + Number(ridge, "slope_z") * z;
            double dx = x - ridgeLine, width = dx < 0 ? Number(ridge, "width_left", 4) : Number(ridge, "width_right", 20), minimum = Clamp(Number(ridge, "min_strength", .5), 0, 1);
            height += Number(ridge, "height") * Math.Exp(-Math.Abs(dx) / Math.Max(.1, Math.Abs(width))) * (minimum + (1 - minimum) * Math.Exp(-Math.Pow((z - Number(ridge, "center_z")) / Math.Max(.1, Math.Abs(Number(ridge, "length_z", 45))), 2)));
        }
        foreach (var feature in riverFeatures)
        {
            double distance = feature.HasCenterline && z >= Number(feature.Data, "min_z", minZ) && z <= Number(feature.Data, "max_z", maxZ) ? Math.Abs(x - CenterlineX(feature, z)) : DistanceToPath(x, z, feature.Path);
            height -= (feature.ValleyDepth ?? Number(feature.Data, "valley_depth")) * Math.Exp(-Math.Pow(distance / Math.Max(.1, Math.Abs(feature.ValleyWidth ?? Number(feature.Data, "valley_width", 4))), 2));
        }
        height += .35 * Math.Sin(x * .18 + z * .11) * Math.Cos(z * .13 - x * .04) + .20 * Math.Sin(x * .39 - z * .17);
        return Clamp(height, Number(terrainSettings, "min_height", .35), Number(terrainSettings, "max_height", 24));
    }
    private static void Triangle(List<float> target, P3 a, P3 b, P3 c, int owner = 0)
    {
        double ux = b.X - a.X, uy = b.Y - a.Y, uz = b.Z - a.Z, vx = c.X - a.X, vy = c.Y - a.Y, vz = c.Z - a.Z;
        double nx = uy * vz - uz * vy, ny = uz * vx - ux * vz, nz = ux * vy - uy * vx, length = Math.Sqrt(nx * nx + ny * ny + nz * nz);
        if (length == 0) length = 1; nx /= length; ny /= length; nz /= length;
        int i = 0;
        foreach (var p in new[] { a, b, c })
        {
            target.Add((float)p.X); target.Add((float)p.Y); target.Add((float)p.Z);
            target.Add((float)nx); target.Add((float)ny); target.Add((float)nz); target.Add(owner);
            target.Add(i == 0 ? 1 : 0); target.Add(i == 1 ? 1 : 0); target.Add(i == 2 ? 1 : 0); i++;
        }
    }
    private static void TopTriangle(List<float> target, P3 a, P3 b, P3 c, int owner = 0)
    {
        if ((b.X - a.X) * (c.Z - a.Z) - (b.Z - a.Z) * (c.X - a.X) > 0) Triangle(target, a, c, b, owner);
        else Triangle(target, a, b, c, owner);
    }
    private void Line(P3 a, P3 b, int owner = 0)
    {
        edges.Add((float)a.X); edges.Add((float)a.Y); edges.Add((float)a.Z); edges.Add(owner);
        edges.Add((float)b.X); edges.Add((float)b.Y); edges.Add((float)b.Z); edges.Add(owner);
    }
    private List<(P3 Left, P3 Right)> Strip(List<float> target, List<P2> points, Func<int, double> width, int owner, double offset)
    {
        var pairs = new List<(P3 Left, P3 Right)>();
        for (int i = 0; i < points.Count; i++)
        {
            var p = points[i]; var a = points[Math.Max(0, i - 1)]; var b = points[Math.Min(points.Count - 1, i + 1)];
            double dx = b.X - a.X, dz = b.Z - a.Z, length = Length(dx, dz); if (length == 0) length = 1;
            double w = width(i); var left = new P2(p.X - dz / length * w, p.Z + dx / length * w); var right = new P2(p.X + dz / length * w, p.Z - dx / length * w);
            pairs.Add((new(left.X, Height(left.X, left.Z) + offset, left.Z), new(right.X, Height(right.X, right.Z) + offset, right.Z)));
            if (i > 0) { var previous = pairs[i - 1]; var current = pairs[i]; TopTriangle(target, previous.Left, previous.Right, current.Right, owner); TopTriangle(target, previous.Left, current.Right, current.Left, owner); }
        }
        return pairs;
    }
    private static List<int[]> Triangulate(List<P2> poly)
    {
        double signed = 0; for (int i = 0; i < poly.Count; i++) { var a = poly[i]; var b = poly[(i + 1) % poly.Count]; signed += a.X * b.Z - b.X * a.Z; }
        int sign = signed > 0 ? 1 : -1; var ids = Enumerable.Range(0, poly.Count).ToList(); var output = new List<int[]>();
        static double Cross(P2 a, P2 b, P2 c) => (b.X - a.X) * (c.Z - b.Z) - (b.Z - a.Z) * (c.X - b.X);
        bool Inside(P2 p, P2 a, P2 b, P2 c) => ((b.X - a.X) * (p.Z - a.Z) - (b.Z - a.Z) * (p.X - a.X)) * sign >= -.0001 &&
            ((c.X - b.X) * (p.Z - b.Z) - (c.Z - b.Z) * (p.X - b.X)) * sign >= -.0001 && ((a.X - c.X) * (p.Z - c.Z) - (a.Z - c.Z) * (p.X - c.X)) * sign >= -.0001;
        int guard = poly.Count * poly.Count;
        while (ids.Count > 3 && guard-- > 0)
        {
            bool clipped = false;
            for (int i = 0; i < ids.Count; i++)
            {
                int ia = ids[(i + ids.Count - 1) % ids.Count], ib = ids[i], ic = ids[(i + 1) % ids.Count];
                if (Cross(poly[ia], poly[ib], poly[ic]) * sign <= .00001) continue;
                if (ids.Any(id => id != ia && id != ib && id != ic && Inside(poly[id], poly[ia], poly[ib], poly[ic]))) continue;
                output.Add([ia, ib, ic]); ids.RemoveAt(i); clipped = true; break;
            }
            if (!clipped) break;
        }
        if (ids.Count == 3) output.Add(ids.ToArray()); return output;
    }
    private void Building(List<P2> poly, double height, int owner)
    {
        if (poly.Count < 3) return;
        var basePoints = poly.Select(p => new P3(p.X, Height(p.X, p.Z) - .04, p.Z)).ToArray();
        double roofY = basePoints.Max(p => p.Y) + height; var roof = poly.Select(p => new P3(p.X, roofY, p.Z)).ToArray();
        foreach (var triangle in Triangulate(poly)) TopTriangle(solids, roof[triangle[0]], roof[triangle[1]], roof[triangle[2]], owner);
        for (int i = 0; i < poly.Count; i++) { int j = (i + 1) % poly.Count; Triangle(solids, basePoints[i], basePoints[j], roof[j], owner); Triangle(solids, basePoints[i], roof[j], roof[i], owner); Line(roof[i], roof[j], owner); Line(basePoints[i], roof[i], owner); }
    }
    private static List<P2> Transformed(double cx, double cz, List<P2> coords, double angle = 0)
    {
        double c = Math.Cos(angle), s = Math.Sin(angle); return coords.Select(p => new P2(cx + p.X * c - p.Z * s, cz + p.X * s + p.Z * c)).ToList();
    }
    private void Rock(double x, double z, double radius, double height, int owner, double seed = 0)
    {
        const int n = 5; var basePoints = new P3[n]; var cap = new P3[n];
        for (int i = 0; i < n; i++)
        {
            double a = (double)i / n * Math.PI * 2, r = radius * (.75 + .35 * Noise(i + seed, 19)), px = x + Math.Cos(a) * r, pz = z + Math.Sin(a) * r;
            basePoints[i] = new(px, Height(px, pz) - .06, pz); cap[i] = new(x + Math.Cos(a) * r * .68, Height(x, z) + height * (.8 + .2 * Noise(seed, i)), z + Math.Sin(a) * r * .68);
        }
        var center = new P3(x, Height(x, z) + height, z);
        for (int i = 0; i < n; i++) { int j = (i + 1) % n; TopTriangle(solids, center, cap[i], cap[j], owner); Triangle(solids, basePoints[i], basePoints[j], cap[j], owner); Triangle(solids, basePoints[i], cap[j], cap[i], owner); Line(cap[i], cap[j], owner); if (i % 2 == 0) Line(basePoints[i], cap[i], owner); }
    }
    private void Tree(double x, double z, double radius, double height, double seed, int owner)
    {
        double ground = Height(x, z); const int n = 5; var ring = new P3[n]; var upper = new P3[n];
        for (int i = 0; i < n; i++)
        {
            double a = 2 * Math.PI * i / n + seed * .31; ring[i] = new(x + Math.Cos(a) * radius, ground + height * .30, z + Math.Sin(a) * radius); upper[i] = new(x + Math.Cos(a) * radius * .48, ground + height * .74, z + Math.Sin(a) * radius * .48);
        }
        var tip = new P3(x + .13, ground + height, z - .12);
        for (int i = 0; i < n; i++) { int j = (i + 1) % n; Triangle(solids, ring[i], ring[j], upper[j], owner); Triangle(solids, ring[i], upper[j], upper[i], owner); Triangle(solids, upper[i], upper[j], tip, owner); Line(ring[i], ring[j], owner); if (i % 2 == 0) { Line(ring[i], upper[i], owner); Line(upper[i], tip, owner); } }
        Line(new(x, ground, z), new(x, ground + height * .32, z), owner);
    }
}
