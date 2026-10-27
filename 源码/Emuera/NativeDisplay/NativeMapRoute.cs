#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace MinorShift.Emuera.NativeDisplay;

// A temporary map plan. Coordinates never mutate the saved scene or character location.
internal sealed class NativeMapRoute
{
    internal const float GroundOffset = .2f;
    private const int MaximumPoints = 4096;
    private readonly NativeSceneWorld world;
    private readonly NativeMapGround ground;
    private readonly List<Vector3> points = [];
    internal IReadOnlyList<Vector3> Points => points;
    internal bool IsDrawing { get; private set; }
    internal bool IsConfirmed { get; private set; }
    internal bool HasRoute => points.Count > 1;
    internal Vector3? EndPoint => HasRoute ? points[^1] : null;
    internal int Version { get; private set; }
    internal float LengthMetres { get; private set; }

    internal NativeMapRoute(NativeSceneWorld world) { this.world = world; ground = new NativeMapGround(world); }

    internal bool Begin()
    {
        var current = world.CurrentMarker;
        if (current == null || !ground.TryPoint(current.X, current.Z, out var start)) return false;
        points.Clear(); points.Add(start + Vector3.UnitY * GroundOffset);
        LengthMetres = 0; IsDrawing = true; IsConfirmed = false; Version++; return true;
    }

    internal bool Append(float x, float z)
    {
        if (!IsDrawing || points.Count == 0 || !ground.TryPoint(x, z, out var end)) return false;
        var start = points[^1];
        float distance = Vector2.Distance(new Vector2(start.X, start.Z), new Vector2(x, z));
        if (distance < .18f) return false;
        int count = Math.Max(1, (int)MathF.Ceiling(distance / .5f));
        bool appended = false;
        for (int i = 1; i <= count && points.Count < MaximumPoints; i++)
        {
            float t = i / (float)count;
            if (!ground.TryPoint(start.X + (end.X - start.X) * t, start.Z + (end.Z - start.Z) * t, out var point)) break;
            point.Y += GroundOffset;
            LengthMetres += Vector3.Distance(points[^1], point) * world.MetersPerUnit;
            points.Add(point); appended = true;
        }
        if (appended) Version++;
        return appended;
    }

    internal void Finish()
    {
        if (!IsDrawing) return;
        IsDrawing = false;
        if (!HasRoute) { points.Clear(); LengthMetres = 0; }
        Version++;
    }

    internal bool Confirm()
    {
        if (!HasRoute || IsDrawing || IsConfirmed) return false;
        IsConfirmed = true; Version++; return true;
    }

    internal bool Clear()
    {
        if (points.Count == 0) return false;
        points.Clear(); LengthMetres = 0; IsDrawing = false; IsConfirmed = false; Version++; return true;
    }

    internal float[] BuildLineVertices()
    {
        if (!HasRoute) return [];
        var data = new List<float>(points.Count * 8 + 160);
        float owner = IsConfirmed ? -4 : -3;
        void Segment(Vector3 a, Vector3 b)
        {
            data.Add(a.X); data.Add(a.Y); data.Add(a.Z); data.Add(owner);
            data.Add(b.X); data.Add(b.Y); data.Add(b.Z); data.Add(owner);
        }
        for (int i = 1; i < points.Count; i++)
            if (IsConfirmed || (i - 1) % 4 < 2) Segment(points[i - 1], points[i]);
        var end = points[^1];
        var behind = points[Math.Max(0, points.Count - 5)];
        var direction = new Vector2(end.X - behind.X, end.Z - behind.Z);
        if (direction.LengthSquared() > .001f)
        {
            direction = Vector2.Normalize(direction);
            var side = new Vector2(-direction.Y, direction.X);
            foreach (int sign in new[] { -1, 1 })
            {
                var wing = new Vector2(end.X, end.Z) - direction * 1.7f + side * sign * .8f;
                Vector3 previous = end;
                for (int i = 1; i <= 6; i++)
                {
                    float t = i / 6f;
                    if (!ground.TryPoint(end.X + (wing.X - end.X) * t, end.Z + (wing.Y - end.Z) * t, out var point)) break;
                    point.Y += GroundOffset; Segment(previous, point); previous = point;
                }
            }
        }
        return data.ToArray();
    }
}

// Interpolate the actual rendered triangles, rather than an approximate analytic
// height field. A small X/Z index keeps ground-following strokes inexpensive.
internal sealed class NativeMapGround
{
    private const int Cells = 32;
    private readonly float[] mesh;
    private readonly Vector4 bounds;
    private readonly List<int>?[] bins = new List<int>?[Cells * Cells];
    internal NativeMapGround(NativeSceneWorld world)
    {
        mesh = world.Terrain; bounds = world.Bounds;
        for (int i = 0; i + 29 < mesh.Length; i += 30)
        {
            int x0 = CellX(Math.Min(mesh[i], Math.Min(mesh[i + 10], mesh[i + 20]))), x1 = CellX(Math.Max(mesh[i], Math.Max(mesh[i + 10], mesh[i + 20])));
            int z0 = CellZ(Math.Min(mesh[i + 2], Math.Min(mesh[i + 12], mesh[i + 22]))), z1 = CellZ(Math.Max(mesh[i + 2], Math.Max(mesh[i + 12], mesh[i + 22])));
            for (int z = z0; z <= z1; z++) for (int x = x0; x <= x1; x++) (bins[z * Cells + x] ??= []).Add(i);
        }
    }
    private int CellX(float x) => Math.Clamp((int)((x - bounds.X) / (bounds.Y - bounds.X) * Cells), 0, Cells - 1);
    private int CellZ(float z) => Math.Clamp((int)((z - bounds.Z) / (bounds.W - bounds.Z) * Cells), 0, Cells - 1);
    internal bool TryPoint(float x, float z, out Vector3 point)
    {
        point = default;
        if (!float.IsFinite(x) || !float.IsFinite(z) || x < bounds.X || x > bounds.Y || z < bounds.Z || z > bounds.W) return false;
        var triangles = bins[CellZ(z) * Cells + CellX(x)]; if (triangles == null) return false;
        foreach (int i in triangles)
        {
            float ax = mesh[i], az = mesh[i + 2], bx = mesh[i + 10], bz = mesh[i + 12], cx = mesh[i + 20], cz = mesh[i + 22];
            float denominator = (bz - cz) * (ax - cx) + (cx - bx) * (az - cz);
            if (Math.Abs(denominator) < .000001f) continue;
            float a = ((bz - cz) * (x - cx) + (cx - bx) * (z - cz)) / denominator;
            float b = ((cz - az) * (x - cx) + (ax - cx) * (z - cz)) / denominator;
            float c = 1 - a - b;
            if (a < -.0001f || b < -.0001f || c < -.0001f) continue;
            point = new Vector3(x, a * mesh[i + 1] + b * mesh[i + 11] + c * mesh[i + 21], z); return true;
        }
        return false;
    }
}
