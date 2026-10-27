#nullable enable
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Windows.Forms;
using MinorShift.Emuera.NativeAI;

namespace MinorShift.Emuera.NativeDisplay;

// A detached, read-only view. Pointer movement never submits a player action or changes a save.
internal sealed class SceneMapPage
{
    private const int PageSize = 8;
    private readonly InventoryState world;
    private readonly Dictionary<int, SceneMapPosition> positions = new();
    private readonly Action close;
    private readonly ScenarioDefinition? scenario;
    private readonly List<(int Id, Rectangle Bounds, PointF Center)> hits = [];
    private readonly List<(int Id, Rectangle Bounds)> labels = [];
    private SceneMapTerrain? surface;
    private Rectangle surfaceArea;
    private int surfacePage = -1;
    internal SceneMapTerrain? Surface => surface;
    private Rectangle back, previous, next, detail;
    private int page, scroll, maxScroll;
    private bool completed;
    private Point? mouse;
    internal int SelectedId { get; private set; }
    internal int? HoveredId { get; private set; }
    internal int PageCount => Math.Max(1, (world.KnownScenes.Count + PageSize - 1) / PageSize);
    internal string CurrentLocation => world.Scene?.Location ?? "尚未确认";
    internal string SelectedName => Selected?.Name ?? "暂无已知地点";
    internal SceneCard? Selected => world.KnownScenes.FirstOrDefault(s => s.Id == SelectedId);
    internal IReadOnlyList<(int Id, Rectangle Bounds, PointF Center)> Targets => hits;
    internal Rectangle ReturnBounds => back;
    internal Rectangle DetailBounds => detail;
    internal int ScrollOffset => scroll;

    internal SceneMapPage(InventoryState state, SceneCatalog? initial, Action close, ScenarioDefinition? scenario = null)
    {
        world = InventoryState.Read(state.Snapshot());
        SceneDescriptionMigrations.Apply(world, initial);
        this.scenario = scenario;
        world.KnownScenes = world.KnownScenes.OrderBy(s => s.Id).ToList();
        this.close = close;
        foreach (var card in world.KnownScenes)
        {
            // Older saves may lack cosmetic coordinates. Read layout hints without importing any initial story/state.
            var hint = card.Map ?? initial?.Scenes.FirstOrDefault(s => s.Name == card.Name || s.Aliases.Contains(card.Name))?.Map;
            if (hint != null) positions[card.Id] = hint.Copy();
        }
        int index = world.KnownScenes.FindIndex(s => s.Name == CurrentLocation);
        if (index < 0) index = 0;
        SelectedId = world.KnownScenes.ElementAtOrDefault(index)?.Id ?? 0;
        page = index / PageSize;
    }

    internal void Move(Point point)
    {
        mouse = point;
        HoveredId = surface?.HitTest(point) ?? labels.Where(h => h.Bounds.Contains(point)).Select(h => (int?)h.Id).FirstOrDefault();
        if (HoveredId is int id && id != SelectedId) { SelectedId = id; scroll = 0; }
    }
    internal void MouseLeave() { mouse = null; HoveredId = null; }
    internal void MouseDown(Point point)
    {
        if (back.Contains(point)) { Close(); return; }
        if (previous.Contains(point)) { ChangePage(-1); return; }
        if (next.Contains(point)) { ChangePage(1); return; }
        Move(point);
    }
    internal void Close() { if (!completed) { completed = true; close(); } }
    internal void Submit(string input)
    {
        if (input.Trim() == "0") Close();
        else if (int.TryParse(input, out int id)) Select(id);
    }
    internal void Select(int id)
    {
        int index = world.KnownScenes.FindIndex(s => s.Id == id);
        if (index < 0) return;
        SelectedId = id; page = index / PageSize; scroll = 0; HoveredId = null; hits.Clear();
    }
    internal void Step(int delta)
    {
        int index = world.KnownScenes.FindIndex(s => s.Id == SelectedId);
        if (world.KnownScenes.Count > 0) Select(world.KnownScenes[Math.Clamp(index + delta, 0, world.KnownScenes.Count - 1)].Id);
    }
    internal void ChangePage(int delta)
    {
        int nextPage = Math.Clamp(page + delta, 0, PageCount - 1);
        if (nextPage == page) return;
        page = nextPage; SelectedId = world.KnownScenes[page * PageSize].Id;
        scroll = 0; HoveredId = null; hits.Clear();
    }
    internal void Wheel(Point point, int delta)
    {
        if (detail.Contains(point)) scroll = Math.Clamp(scroll - Math.Sign(delta) * 60, 0, maxScroll);
        else ChangePage(delta > 0 ? -1 : 1);
    }

    internal void Draw(Graphics g, Rectangle area, Font original, DisplayTheme theme, int lineHeight)
    {
        if (area.Width < 1 || area.Height < 1) return;
        var saved = g.Save();
        try
        {
            g.Clear(theme.BackgroundColor);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            int line = Math.Clamp(Math.Min(lineHeight, area.Height / 20), 17, 34);
            int pad = Math.Clamp(area.Width / 45, 12, 32);
            using var font = new Font(original.FontFamily, Math.Max(12, Math.Min(original.Size, line * .76f)), FontStyle.Regular, GraphicsUnit.Pixel);
            using var small = new Font(original.FontFamily, Math.Max(10, font.Size * .75f), FontStyle.Regular, GraphicsUnit.Pixel);
            using var title = new Font(original.FontFamily, font.Size * 1.13f, FontStyle.Bold, GraphicsUnit.Pixel);
            Color lineColor = theme.TextColor;
            var focus = ColorSafety.Selection(theme);
            Text(g, "场景与人物所在地", title, new(pad, 12, area.Width - pad * 2 - 100, line + 6), theme.TextColor);
            Text(g, "当前地点：" + CurrentLocation, small, new(pad, line + 22, area.Width - pad * 2, line), theme.TextColor);
            int mapTop = line * 2 + 28;
            int footer = line + 16;
            int mapHeight = Math.Max(85, (int)((area.Height - mapTop - footer) * .61));
            var map = new Rectangle(pad, mapTop, Math.Max(1, area.Width - pad * 2), mapHeight);
            hits.Clear(); labels.Clear(); back = previous = next = Rectangle.Empty;
            var cards = world.KnownScenes.Skip(page * PageSize).Take(PageSize).ToArray();
            if (surface == null || surfaceArea != map || surfacePage != page)
            {
                bool authored = cards.All(c => positions.ContainsKey(c.Id));
                int columns = cards.Length <= 6 ? 3 : 4;
                var sites = cards.Select((card, index) =>
                {
                    var position = authored ? positions[card.Id] : new SceneMapPosition
                    {
                        X = .14 + index % columns * (.72 / (columns - 1)),
                        Y = cards.Length <= columns ? .47 : index < columns ? .23 : .76
                    };
                    return new SceneMapSite(card.Id, position.X, position.Y,
                        positions.GetValueOrDefault(card.Id)?.Symbol ?? "place", card.Adjacent.ToArray());
                }).ToArray();
                surface = new SceneMapTerrain(map, sites);
                surfaceArea = map; surfacePage = page;
            }
            surface.Draw(g, theme, HoveredId);
            var mapState = g.Save();
            g.SetClip(map, CombineMode.Intersect);
            foreach (var region in surface.Regions)
            {
                hits.Add((region.Id, region.Bounds, region.Center));
                var card = cards.First(c => c.Id == region.Id);
                int width = Math.Min(map.Width / 3, TextRenderer.MeasureText(card.Name, font).Width + 16);
                int labelTop = region.Kind == "stream" ? (int)region.Center.Y + line / 2 : region.Bounds.Bottom + 4;
                labelTop = Math.Clamp(labelTop, map.Top, Math.Max(map.Top, map.Bottom - line * 2));
                int labelLeft = Math.Clamp((int)region.Center.X - width / 2, map.Left + 3, Math.Max(map.Left + 3, map.Right - width - 3));
                var label = new Rectangle(labelLeft, labelTop, width, line);
                // Prefer the nearby empty space when an earlier name would overlap.
                for (int attempt = 0; attempt < 3 && labels.Any(l => l.Bounds.IntersectsWith(label)); attempt++)
                    label.Y = Math.Max(map.Top, label.Y - line - 3);
                labels.Add((card.Id, label));
                Text(g, card.Name, font, label, HoveredId == card.Id ? focus : theme.TextColor, true);
                if (card.Name == CurrentLocation)
                {
                    float marker = Math.Clamp(map.Height / 450f, .4f, 1.3f);
                    using var pen = new Pen(focus, 1.7f);
                    g.DrawEllipse(pen, region.Center.X - 6 * marker, region.Center.Y - 3 * marker, 12 * marker, 6 * marker);
                    g.DrawLine(pen, region.Center.X, region.Center.Y, region.Center.X, region.Center.Y - 32 * marker);
                }
            }
            if (cards.Length == 0) Text(g, "尚未记录地点", font, map, theme.TextColor, true);
            Text(g, "已知地点 " + world.KnownScenes.Count + (PageCount > 1 ? $"  ·  {page + 1}/{PageCount}" : ""), small,
                new(map.Left + 10, map.Bottom - line, map.Width / 2, line), SceneMapTerrain.Tint(theme, .7));
            if (PageCount > 1)
            {
                previous = new(map.Right - 180, map.Bottom - line - 2, 82, line);
                next = new(map.Right - 90, map.Bottom - line - 2, 82, line);
                Text(g, "〈 上一页", small, previous, mouse is Point p && previous.Contains(p) ? focus : theme.TextColor);
                Text(g, "下一页 〉", small, next, mouse is Point n && next.Contains(n) ? focus : theme.TextColor);
            }
            g.Restore(mapState);
            int divider = map.Bottom + 9;
            using (var rule = new Pen(SceneMapTerrain.Tint(theme, .65))) g.DrawLine(rule, pad, divider, area.Right - pad, divider);
            detail = new(pad, divider + 12, Math.Max(1, area.Width - pad * 2), Math.Max(1, area.Bottom - footer - divider - 18));
            DrawDetail(g, detail, font, title, small, line, theme.TextColor, lineColor, focus);
            back = new(pad, area.Bottom - footer, Math.Min(area.Width - pad * 2, TextRenderer.MeasureText("[0] 返回行动菜单", font).Width + 15), footer);
            Text(g, "[0] 返回行动菜单", font, back, mouse is Point m && back.Contains(m) ? focus : theme.TextColor);
        }
        finally { g.Restore(saved); }
    }

    private void DrawDetail(Graphics g, Rectangle bounds, Font font, Font title, Font small, int line, Color text, Color lineColor, Color focus)
    {
        var card = Selected;
        var state = g.Save();
        g.SetClip(bounds, CombineMode.Intersect);
        int top = bounds.Top - scroll;
        Text(g, SelectedName + (card?.Name == CurrentLocation ? "  ·  当前位置" : ""), title, new(bounds.X, top, bounds.Width, line + 4), focus);
        top += line + 8;
        void Paragraph(string value, Color color)
        {
            if (value.Length == 0) return;
            int width = Math.Max(1, bounds.Width - 16);
            var flags = TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding | TextFormatFlags.WordBreak | TextFormatFlags.TextBoxControl | TextFormatFlags.PreserveGraphicsClipping;
            int height = TextRenderer.MeasureText(value, font, new Size(width, int.MaxValue), flags).Height + 8;
            TextRenderer.DrawText(g, value, font, new Rectangle(bounds.Left, top, width, height), color, flags);
            top += height;
        }
        if (card != null)
        {
            Paragraph(card.Description.Length > 0 ? card.Description : "这个地点尚无详细记录。", text);
            var people = world.Characters.Where(c => world.CharacterLocations.GetValueOrDefault(c.Key) == card.Name ||
                !world.CharacterLocations.ContainsKey(c.Key) && world.Scene?.Location == card.Name && world.Scene.PresentCharacters.Contains(c.Key)).Select(c => c.Value).ToArray();
            Paragraph("这里的人物：" + (people.Length > 0 ? string.Join("、", people) : "未见人物"), text);
            var actions = SceneActionPresentation.ForScene(world, scenario, card);
            Paragraph("可进行的行动：" + (actions.Length > 0 ? string.Join("、", actions) : "无"), text);
            var benefits = SceneActionPresentation.BenefitsForScene(world, scenario, card);
            Paragraph("特殊增益：" + (benefits.Length > 0 ? string.Join("、", benefits) : "无"), text);
        }
        else Paragraph("已知地点会随本局的探索记录在这里。", text);
        maxScroll = Math.Max(0, top + scroll - bounds.Bottom);
        scroll = Math.Min(scroll, maxScroll);
        g.Restore(state);
        if (maxScroll > 0)
        {
            int height = Math.Max(15, bounds.Height * bounds.Height / (bounds.Height + maxScroll));
            int y = bounds.Top + (bounds.Height - height) * scroll / maxScroll;
            using var brush = new SolidBrush(Color.FromArgb(130, lineColor));
            g.FillRectangle(brush, bounds.Right - 4, y, 2, height);
        }
    }

    private static void Text(Graphics g, string value, Font font, Rectangle bounds, Color color, bool center = false) =>
        TextRenderer.DrawText(g, value, font, bounds, color, TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix |
            TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.VerticalCenter | TextFormatFlags.PreserveGraphicsClipping |
            (center ? TextFormatFlags.HorizontalCenter : TextFormatFlags.Left));
}
