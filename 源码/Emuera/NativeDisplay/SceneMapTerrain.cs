#nullable enable
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;

namespace MinorShift.Emuera.NativeDisplay;

internal sealed record SceneMapSite(int Id, double X, double Y, string Kind, int[] Adjacent);

internal sealed class SceneMapRegion(int id, PointF center, Rectangle bounds, double depth, string kind, List<PointF[]> faces)
{
    internal int Id { get; } = id;
    internal PointF Center { get; } = center;
    internal Rectangle Bounds { get; } = bounds;
    internal double Depth { get; } = depth;
    internal string Kind { get; } = kind;
    internal IReadOnlyList<PointF[]> Faces { get; } = faces;
    internal bool Contains(PointF point) => Bounds.Contains(Point.Round(point)) && Faces.Any(face => SceneMapTerrain.Inside(face, point));
}

// World-space relief is projected once per viewport. Paint resolves the current project theme.
internal sealed class SceneMapTerrain
{
    private readonly record struct Vertex(double X, double Y, double Z);
    private sealed record Face(PointF[] Points, double Light, int Owner, double Depth);
    private sealed record Road(int A, int B, PointF[] Left, PointF[] Right);
    private readonly Rectangle area;
    private readonly SceneMapSite[] sites;
    private readonly List<Face> terrain = [], structures = [], water = [];
    private readonly List<Road> roads = [];
    private readonly List<SceneMapRegion> regions = [];
    internal IReadOnlyList<SceneMapRegion> Regions => regions;
    internal double ReliefRange { get; private set; }
    internal int TriangleCount => terrain.Count;

    internal SceneMapTerrain(Rectangle area, SceneMapSite[] sites)
    {
        this.area = area; this.sites = sites;
        BuildTerrain();
        foreach (var site in sites.Where(s => s.Kind == "stream")) BuildRiver(site);
        BuildRoads();
        foreach (var site in sites.Where(s => s.Kind != "stream")) BuildRegion(site);
        structures.Sort((a, b) => a.Depth.CompareTo(b.Depth));
    }
    internal int? HitTest(Point point)
    {
        foreach (var region in regions.Where(r => r.Kind != "stream").OrderByDescending(r => r.Depth))
            if (region.Contains(point)) return region.Id;
        foreach (var region in regions.Where(r => r.Kind == "stream"))
            if (region.Contains(point)) return region.Id;
        return null;
    }
    internal static bool Inside(PointF[] polygon, PointF point)
    {
        bool inside = false;
        for (int i = 0, j = polygon.Length - 1; i < polygon.Length; j = i++)
        {
            var a = polygon[i]; var b = polygon[j];
            if ((a.Y > point.Y) != (b.Y > point.Y) && point.X < (b.X - a.X) * (point.Y - a.Y) / (b.Y - a.Y) + a.X)
                inside = !inside;
        }
        return inside;
    }
    private PointF Project(Vertex p) => new(
        area.Left + (float)(.50 + (p.X - .5) * (.86 + p.Y * .18) + (p.Y - .5) * .10) * area.Width,
        area.Top + (float)(.23 + p.Y * .72 - p.Z * .58 + (p.X - .5) * .04) * area.Height);

    private double Height(double x, double y)
    {
        static double Hill(double x, double y, double cx, double cy, double rx, double ry, double h) =>
            h * Math.Exp(-((x - cx) * (x - cx) / (rx * rx) + (y - cy) * (y - cy) / (ry * ry)));
        double z = .035 + Hill(x, y, .81, .16, .20, .23, .28) + Hill(x, y, .11, .12, .20, .25, .20)
            + Hill(x, y, .96, .84, .18, .28, .22) + Hill(x, y, .02, .90, .18, .23, .16)
            + .017 * Math.Sin(x * 19 + y * 4) * Math.Cos(y * 17 - x * 3);
        foreach (var stream in sites.Where(s => s.Kind == "stream"))
        {
            double distance = x - RiverX(stream, y);
            z *= 1 - .80 * Math.Exp(-distance * distance / .0016);
        }
        return Math.Max(.008, z);
    }
    private static double RiverX(SceneMapSite site, double y) => site.X + .052 * Math.Sin((y - site.Y) * 11) - .12 * (y - site.Y);
    private Vertex Ground(double x, double y, double offset = 0) => new(x, y, Height(x, y) + offset);

    private void BuildTerrain()
    {
        const int cols = 38, rows = 28;
        var vertices = new Vertex[rows + 1, cols + 1];
        double min = 10, max = -10;
        for (int row = 0; row <= rows; row++)
            for (int col = 0; col <= cols; col++)
            {
                double x = -.20 + col * 1.40 / cols + Math.Sin(row * 7.7 + col * 2.3) * .009;
                double y = -.40 + row * 1.90 / rows + Math.Cos(col * 4.1 + row * 3.7) * .012;
                vertices[row, col] = Ground(x, y);
                min = Math.Min(min, vertices[row, col].Z); max = Math.Max(max, vertices[row, col].Z);
            }
        ReliefRange = max - min;
        for (int row = 0; row < rows; row++)
            for (int col = 0; col < cols; col++)
            {
                var a = vertices[row, col]; var b = vertices[row, col + 1];
                var c = vertices[row + 1, col]; var d = vertices[row + 1, col + 1];
                if ((row + col) % 2 == 0) { Add(terrain, [a, b, c]); Add(terrain, [b, d, c]); }
                else { Add(terrain, [a, b, d]); Add(terrain, [a, d, c]); }
            }
    }
    private void BuildRiver(SceneMapSite site)
    {
        var left = new List<Vertex>(); var right = new List<Vertex>();
        for (int i = 0; i <= 55; i++)
        {
            double y = -.18 + i * 1.45 / 55, x = RiverX(site, y);
            double width = .015 + .004 * Math.Sin(y * 9 + 2), z = Height(x, y) + .006;
            left.Add(new(x - width, y, z)); right.Add(new(x + width, y, z));
        }
        int start = water.Count;
        for (int i = 0; i + 1 < left.Count; i++) Add(water, [left[i], right[i], right[i + 1], left[i + 1]], site.Id);
        var faces = water.Skip(start).Select(f => f.Points).ToList();
        regions.Add(new(site.Id, Project(Ground(site.X, site.Y, .007)), Bounds(faces), site.Y, site.Kind, faces));
    }
    private void BuildRoads()
    {
        foreach (var a in sites)
            foreach (int other in a.Adjacent)
            {
                var b = sites.FirstOrDefault(s => s.Id == other);
                if (b == null || a.Id > b.Id && b.Adjacent.Contains(a.Id)) continue;
                var left = new PointF[20]; var right = new PointF[20];
                double dx = b.X - a.X, dy = b.Y - a.Y, length = Math.Max(.01, Math.Sqrt(dx * dx + dy * dy));
                for (int i = 0; i < 20; i++)
                {
                    double t = i / 19d, bend = Math.Sin(t * Math.PI) * .027 * (a.Id % 2 == 0 ? 1 : -1);
                    double x = a.X + dx * t - dy / length * bend, y = a.Y + dy * t + dx / length * bend;
                    left[i] = Project(Ground(x - dy / length * .004, y + dx / length * .004, .004));
                    right[i] = Project(Ground(x + dy / length * .004, y - dx / length * .004, .004));
                }
                roads.Add(new(a.Id, b.Id, left, right));
            }
    }
    private void BuildRegion(SceneMapSite site)
    {
        double density = sites.Length > 5 ? .64 : 1, variation = .86 + Noise(site.Id, 5) * .28;
        (double rx, double ry, double rise) = site.Kind switch
        {
            "forest" => (.155, .19, .075), "market" => (.130, .145, .048),
            "cabin" => (.074, .102, .032), "path" => (.100, .062, .010),
            _ => (.07 + Noise(site.Id, 3) * .05, .07 + Noise(site.Id, 4) * .08, .025 + Noise(site.Id, 7) * .04)
        };
        rx *= variation * density; ry *= variation * density;
        int count = site.Kind == "market" ? 10 : 7 + site.Id % 4;
        var ground = new Vertex[count];
        for (int i = 0; i < count; i++)
        {
            double angle = i * Math.PI * 2 / count + (Noise(site.Id, i + 30) - .5) * .20;
            double radius = .78 + Noise(site.Id, i + 60) * .28;
            ground[i] = Ground(site.X + Math.Cos(angle) * rx * radius, site.Y + Math.Sin(angle) * ry * radius);
        }
        double topZ = ground.Max(p => p.Z) + rise;
        var top = ground.Select(p => new Vertex(p.X, p.Y, topZ)).ToArray();
        int start = structures.Count;
        var peak = new Vertex(site.X - rx * .12, site.Y - ry * .08, topZ + (site.Kind == "forest" ? .075 : .003));
        for (int i = 0; i < count; i++)
        {
            int next = (i + 1) % count;
            Add(structures, [ground[i], ground[next], top[next], top[i]], site.Id);
            Add(structures, [top[i], top[next], peak], site.Id);
        }
        if (site.Kind == "cabin")
        {
            Building(site.Id, site.X - rx * .12, site.Y - ry * .08, topZ, rx * .65, ry * .52, .060, true);
            Rock(site.Id, site.X + rx * .62, site.Y + ry * .26, topZ, rx * .14, .025);
        }
        else if (site.Kind == "market")
        {
            for (int i = 0; i < 6; i++)
            {
                double x = site.X + (i % 3 - 1) * rx * .47, y = site.Y + (i / 3 - .5) * ry * .72;
                Building(site.Id, x, y, topZ, rx * (.21 + Noise(site.Id, i) * .14),
                    ry * (.24 + Noise(site.Id, i + 10) * .15), .04 + Noise(site.Id, i + 20) * .055, i % 2 == 0);
            }
        }
        else if (site.Kind == "forest")
        {
            for (int i = 0; i < 9; i++)
            {
                double angle = i * 2.4, radius = .2 + Noise(site.Id, i) * .48;
                double x = site.X + Math.Cos(angle) * rx * radius, y = site.Y + Math.Sin(angle) * ry * radius;
                Rock(site.Id, x, y, topZ + .075 * (1 - radius), rx * (.09 + Noise(i, 2) * .05), .045 + Noise(i, 3) * .055);
            }
        }
        else if (site.Kind != "path") Rock(site.Id, site.X, site.Y, topZ, rx * .42, .04 + Noise(site.Id, 9) * .055);
        var faces = structures.Skip(start).Select(f => f.Points).ToList();
        regions.Add(new(site.Id, Project(new(site.X, site.Y, topZ + .004)), Bounds(faces), site.Y, site.Kind, faces));
    }
    private void Building(int owner, double x, double y, double ground, double w, double d, double height, bool roof)
    {
        Vertex[] bottom = [new(x - w / 2, y - d / 2, ground), new(x + w / 2, y - d / 2, ground),
            new(x + w / 2, y + d / 2, ground), new(x - w / 2, y + d / 2, ground)];
        var top = bottom.Select(p => p with { Z = ground + height }).ToArray();
        for (int i = 0; i < 4; i++) Add(structures, [bottom[i], bottom[(i + 1) % 4], top[(i + 1) % 4], top[i]], owner);
        if (roof)
        {
            var a = new Vertex(x - w / 2, y, ground + height * 1.42); var b = new Vertex(x + w / 2, y, ground + height * 1.42);
            Add(structures, [top[0], top[1], b, a], owner); Add(structures, [a, b, top[2], top[3]], owner);
            Add(structures, [top[0], a, top[3]], owner); Add(structures, [top[1], top[2], b], owner);
        }
        else Add(structures, top, owner);
    }
    private void Rock(int owner, double x, double y, double z, double radius, double height)
    {
        var peak = new Vertex(x - radius * .24, y - radius * .18, z + height);
        for (int i = 0; i < 5; i++)
        {
            double a = i * Math.PI * 2 / 5, b = (i + 1) * Math.PI * 2 / 5;
            Add(structures, [new(x + Math.Cos(a) * radius, y + Math.Sin(a) * radius * 1.4, z),
                new(x + Math.Cos(b) * radius, y + Math.Sin(b) * radius * 1.4, z), peak], owner);
        }
    }
    private void Add(List<Face> faces, Vertex[] vertices, int owner = 0)
    {
        var a = vertices[0]; var b = vertices[1]; var c = vertices[2];
        double ux = b.X - a.X, uy = b.Y - a.Y, uz = b.Z - a.Z, vx = c.X - a.X, vy = c.Y - a.Y, vz = c.Z - a.Z;
        double nx = uy * vz - uz * vy, ny = uz * vx - ux * vz, nz = ux * vy - uy * vx;
        if (nz < 0) { nx = -nx; ny = -ny; nz = -nz; }
        double length = Math.Max(.000001, Math.Sqrt(nx * nx + ny * ny + nz * nz));
        double light = Math.Clamp((nx * -.40 + ny * -.55 + nz * .73) / length, 0, 1);
        faces.Add(new(vertices.Select(Project).ToArray(), light, owner, vertices.Average(p => p.Y) + vertices.Average(p => p.Z) * .02));
    }
    private Rectangle Bounds(IEnumerable<PointF[]> faces)
    {
        var pts = faces.SelectMany(p => p).ToArray();
        if (pts.Length == 0) return Rectangle.Empty;
        return Rectangle.Intersect(area, Rectangle.Ceiling(RectangleF.FromLTRB(pts.Min(p => p.X), pts.Min(p => p.Y), pts.Max(p => p.X), pts.Max(p => p.Y))));
    }
    private static double Noise(int a, int b)
    { double n = Math.Sin(a * 37.13 + b * 17.79 + 1.3) * 43758.5453; return n - Math.Floor(n); }

    internal static Color Tint(DisplayTheme theme, double amount, bool focus = false)
    {
        Color a = theme.BackgroundColor, b = focus ? ColorSafety.Selection(theme) : theme.TextColor;
        return Color.FromArgb((int)Math.Round(a.R + (b.R - a.R) * amount), (int)Math.Round(a.G + (b.G - a.G) * amount),
            (int)Math.Round(a.B + (b.B - a.B) * amount));
    }
    internal void Draw(Graphics g, DisplayTheme theme, int? hovered)
    {
        var saved = g.Save();
        try
        {
            g.SetClip(area, CombineMode.Intersect);
            bool lightTheme = ColorSafety.Luminance(theme.BackgroundColor) > ColorSafety.Luminance(theme.TextColor);
            using var mesh = new Pen(Tint(theme, .24), .65f);
            foreach (var face in terrain)
            {
                double shade = lightTheme ? .10 + .19 * (1 - face.Light) : .055 + .19 * face.Light;
                using var fill = new SolidBrush(Tint(theme, shade));
                g.FillPolygon(fill, face.Points); g.DrawPolygon(mesh, face.Points);
            }
            int ripple = 0;
            foreach (var face in water)
            {
                bool active = hovered == face.Owner;
                using var fill = new SolidBrush(Tint(theme, active ? .37 : .30, active));
                g.FillPolygon(fill, face.Points);
                using var outline = new Pen(Tint(theme, active ? .95 : .63, active), Math.Max(.8f, area.Height / 800f));
                g.DrawLine(outline, face.Points[0], face.Points[3]); g.DrawLine(outline, face.Points[1], face.Points[2]);
                if (ripple++ % 3 != 0)
                {
                    var a = face.Points[0]; var b = face.Points[1]; var c = face.Points[2]; var d = face.Points[3];
                    using var flow = new Pen(Tint(theme, active ? .72 : .51, active), Math.Max(.6f, area.Height / 900f));
                    g.DrawLine(flow, a.X * .65f + b.X * .35f, a.Y * .65f + b.Y * .35f,
                        d.X * .6f + c.X * .4f, d.Y * .6f + c.Y * .4f);
                    g.DrawLine(flow, a.X * .3f + b.X * .7f, a.Y * .3f + b.Y * .7f,
                        d.X * .36f + c.X * .64f, d.Y * .36f + c.Y * .64f);
                }
            }
            foreach (var road in roads)
            {
                bool active = hovered == road.A || hovered == road.B;
                using var fill = new SolidBrush(Tint(theme, active ? .55 : .22, active));
                g.FillPolygon(fill, road.Left.Concat(road.Right.Reverse()).ToArray());
                using var edge = new Pen(Tint(theme, active ? .85 : .38, active), active ? 1.1f : .6f);
                g.DrawLines(edge, road.Left); g.DrawLines(edge, road.Right);
            }
            foreach (var face in structures)
            {
                bool active = hovered == face.Owner;
                double shade = lightTheme ? .23 + .26 * (1 - face.Light) : .20 + .30 * face.Light;
                using var fill = new SolidBrush(Tint(theme, shade, active));
                using var edge = new Pen(Tint(theme, active ? 1 : .85, active), active ? 1.5f : .95f) { LineJoin = LineJoin.Round };
                g.FillPolygon(fill, face.Points); g.DrawPolygon(edge, face.Points);
            }
            using var border = new Pen(Tint(theme, .4));
            g.DrawRectangle(border, area.Left, area.Top, Math.Max(1, area.Width - 1), Math.Max(1, area.Height - 1));
        }
        finally { g.Restore(saved); }
    }
}
