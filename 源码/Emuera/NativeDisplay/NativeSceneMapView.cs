#nullable enable
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Linq;
using System.Numerics;
using System.Windows.Forms;

namespace MinorShift.Emuera.NativeDisplay;

// Presentation emits a route to the host; only the host validates and commits movement.
internal sealed class NativeSceneMapView : UserControl
{
    private readonly Action close;
    private readonly Action<int> selected;
    private readonly string currentLocation;
    private readonly Panel detail = new() { AutoScroll = true, TabStop = true };
    private readonly Panel content = new();
    private readonly Label routeStatus = new() { TextAlign = ContentAlignment.MiddleRight, AutoSize = false, AutoEllipsis = true, UseMnemonic = false };
    private readonly List<Control> paragraphs = [];
    private readonly Font headingFont = new("Microsoft YaHei UI", 12.75f);
    private readonly Font bodyFont = new("Microsoft YaHei UI", 10.5f);
    private readonly Font smallFont = new("Microsoft YaHei UI", 9f);
    private int detailId = -1;
    private int? detailDestination;
    private bool closing;
    private Func<IReadOnlyList<Vector3>, TravelQuote>? travelQuote;
    private Action<IReadOnlyList<Vector3>>? travel;
    private string travelInstruction = "";
    internal Button TravelButton { get; } = new() { Text = "开始移动", FlatStyle = FlatStyle.Flat, Enabled = false, Visible = false };
    internal NativeSceneWorld World { get; }
    internal NativeMapSurface Surface { get; }
    internal bool Ready => Surface.Ready;
    internal DisplayTheme Theme { get; private set; }
    internal int SelectedId { get; private set; }
    internal int? DestinationId { get; private set; }
    internal Rectangle MapBounds => Surface.Bounds;
    internal Rectangle DetailBounds => detail.Bounds;
    internal Panel DetailControl => detail;
    internal int DetailScrollOffset => -detail.AutoScrollPosition.Y;
    internal string DetailText { get; private set; } = "";
    internal Button ReturnButton { get; } = new() { Text = "[0] 返回行动菜单", FlatStyle = FlatStyle.Flat, AutoSize = true };
    internal Button DestinationButton { get; } = new() { Text = "标记目的地", FlatStyle = FlatStyle.Flat };
    internal long GeometryBytes => Surface.GeometryBytes;
    private int Unit(float value) => Math.Max(1, (int)Math.Round(value * DeviceDpi / 96f));

    internal NativeSceneMapView(NativeSceneWorld world, string currentLocation, DisplayTheme theme,
        Action close, Action<int> selected, Action<string> failed)
    {
        World = world; this.currentLocation = currentLocation; this.close = close; this.selected = selected;
        Theme = theme.Safe(); AutoScaleMode = AutoScaleMode.None;
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint, true);
        SelectedId = world.CurrentId != 0 ? world.CurrentId : world.Sites.FirstOrDefault()?.Id ?? 0;
        Surface = new NativeMapSurface(world, Theme, failed);
        Surface.HoverChanged += _ => ShowDetails();
        Surface.Selected += Select;
        Surface.RouteChanged += () =>
        {
            if (IsDisposed || Disposing) return;
            DestinationId = null; Surface.DestinationId = null;
            RefreshRouteStatus();
            ShowDetails();
        };
        Surface.ActiveSiteId = SelectedId;
        Controls.Add(Surface); Controls.Add(detail); Controls.Add(ReturnButton); Controls.Add(routeStatus); Controls.Add(TravelButton);
        detail.Controls.Add(content);
        detail.Resize += (_, _) => LayoutDetails();
        ReturnButton.FlatAppearance.BorderSize = 0;
        ReturnButton.Font = bodyFont; ReturnButton.Click += (_, _) => RequestClose();
        routeStatus.Font = smallFont;
        TravelButton.Font = bodyFont;
        TravelButton.Click += (_, _) =>
        {
            if (closing || travel == null || !Surface.Route.IsConfirmed || travelQuote?.Invoke(Surface.Route.Points).Allowed != true) return;
            closing = true;
            var points = Surface.Route.Points.ToArray();
            BeginInvoke(() => { if (!IsDisposed) travel(points); });
        };
        DestinationButton.Font = smallFont; DestinationButton.AutoSize = false;
        DestinationButton.Click += (_, _) => MarkDestination(Surface.HoveredId ?? SelectedId);
        UpdateTheme(Theme); ShowDetails();
    }

    internal void EnableTravel(Func<IReadOnlyList<Vector3>, TravelQuote> quote, Action<IReadOnlyList<Vector3>> execute, string instruction = "按住右键画路线，左键确认后开始移动")
    {
        travelQuote = quote; travel = execute; travelInstruction = instruction; TravelButton.Visible = true;
        PerformLayout(); RefreshRouteStatus();
    }
    private void RefreshRouteStatus()
    {
        var route = Surface.Route;
        var quote = route.HasRoute ? travelQuote?.Invoke(route.Points) : null;
        TravelButton.Enabled = route.IsConfirmed && quote?.Allowed == true;
        routeStatus.Text = route.HasRoute ? (route.IsDrawing ? "正在绘制" : route.IsConfirmed ? "路线已确认" : "左键单击确认路线") + " · " +
            (quote?.Text ?? $"约 {route.LengthMetres:0.#} 米") : travel != null ? travelInstruction : "";
    }

    protected override void OnLayout(LayoutEventArgs e)
    {
        base.OnLayout(e);
        if (Surface == null) return;
        int header = Unit(44), footer = Unit(travel == null ? 36 : 72), available = Math.Max(2, ClientSize.Height - header - footer);
        int mapHeight = Math.Max(1, (int)Math.Round(available * .7));
        Surface.Bounds = new Rectangle(0, header, ClientSize.Width, mapHeight);
        detail.Bounds = new Rectangle(Unit(22), header + mapHeight, Math.Max(1, ClientSize.Width - Unit(44)), Math.Max(1, available - mapHeight));
        ReturnButton.Bounds = new Rectangle(Unit(22), header + available, Unit(195), footer);
        routeStatus.Bounds = new Rectangle(Unit(225), header + available, Math.Max(1, ClientSize.Width - Unit(247)), footer);
        if (travel != null)
        {
            routeStatus.Bounds = new Rectangle(Unit(22), header + available, Math.Max(1, Width - Unit(44)), Unit(32));
            ReturnButton.Top += Unit(36); ReturnButton.Height = Unit(36);
            TravelButton.Bounds = new Rectangle(Math.Max(Unit(225), Width - Unit(175)), ReturnButton.Top, Unit(150), Unit(32));
        }
        ReturnButton.TextAlign = ContentAlignment.MiddleLeft;
        LayoutDetails();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var area = new Rectangle(Unit(22), Unit(5), Math.Max(1, Width - Unit(44)), Unit(32));
        TextRenderer.DrawText(e.Graphics, "场景地图", bodyFont, area, Theme.TextColor, TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
        TextRenderer.DrawText(e.Graphics, "当前地点：" + (string.IsNullOrEmpty(currentLocation) ? "尚未确认" : currentLocation), smallFont,
            area, Blend(Theme.BackgroundColor, Theme.TextColor, .7f), TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
    }

    internal void Select(int id)
    {
        if (!World.Sites.Any(s => s.Id == id)) return;
        SelectedId = id; Surface.ActiveSiteId = id; Surface.ClearHover(); selected(id); ShowDetails(); Surface.Redraw();
    }

    internal void MarkDestination(int id)
    {
        if (id == World.CurrentId || !World.Sites.Any(s => s.Id == id)) return;
        Surface.ClearRoute(); DestinationId = id; SelectedId = id; Surface.ActiveSiteId = id;
        Surface.DestinationId = id; Surface.ClearHover(); selected(id); ShowDetails(); Surface.Redraw();
    }

    internal void RequestClose()
    {
        if (closing || IsDisposed) return;
        closing = true;
        if (IsHandleCreated) BeginInvoke(() => { if (!IsDisposed) close(); });
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData is Keys.Escape or Keys.D0 or Keys.NumPad0) { RequestClose(); return true; }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    internal void UpdateTheme(DisplayTheme theme)
    {
        Theme = theme.Safe(); BackColor = detail.BackColor = content.BackColor = Theme.BackgroundColor;
        ForeColor = ReturnButton.ForeColor = DestinationButton.ForeColor = Theme.TextColor;
        ReturnButton.BackColor = DestinationButton.BackColor = Theme.BackgroundColor;
        TravelButton.BackColor = Theme.BackgroundColor; TravelButton.ForeColor = Theme.TextColor;
        TravelButton.FlatAppearance.BorderColor = ColorSafety.Selection(Theme);
        routeStatus.BackColor = Theme.BackgroundColor; routeStatus.ForeColor = ColorSafety.Selection(Theme);
        DestinationButton.FlatAppearance.BorderColor = Blend(Theme.BackgroundColor, Theme.TextColor, .4f);
        foreach (var p in paragraphs) p.ForeColor = p.Tag is true ? ColorSafety.Selection(Theme) : Theme.TextColor;
        Surface.UpdateTheme(Theme); Invalidate(); detail.Invalidate();
    }

    private void ShowDetails()
    {
        var site = World.Sites.FirstOrDefault(s => s.Id == (Surface.HoveredId ?? SelectedId));
        int id = site?.Id ?? 0;
        if (detailId == id && detailDestination == DestinationId) return;
        bool changed = detailId != id; detailId = id; detailDestination = DestinationId;
        if (changed) detail.AutoScrollPosition = Point.Empty;
        content.SuspendLayout();
        content.Controls.Remove(DestinationButton);
        foreach (var p in paragraphs) p.Dispose(); paragraphs.Clear();
        void Paragraph(string text, Font font, bool focus = false)
        {
            if (string.IsNullOrEmpty(text)) return;
            var label = new Label { Text = text, AutoSize = false, UseMnemonic = false, Font = font, Tag = focus,
                ForeColor = focus ? ColorSafety.Selection(Theme) : Theme.TextColor, BackColor = Theme.BackgroundColor };
            paragraphs.Add(label); content.Controls.Add(label);
        }
        if (site == null) Paragraph("尚未记录地点", headingFont, true);
        else
        {
            Paragraph(Surface.SiteTitle(site) + (site.Id == World.CurrentId ? "   当前位置" : site.Id == DestinationId ? "   目的地预览" : ""), headingFont, true);
            Paragraph(site.Description, bodyFont);
            Paragraph("这里的人物：" + (site.People.Length > 0 ? string.Join("、", site.People) : "未见人物"), smallFont);
            Paragraph("可进行的行动：" + (site.Actions.Length > 0 ? string.Join("、", site.Actions) : "无"), smallFont);
            Paragraph("特殊增益：" + (site.Benefits.Length > 0 ? string.Join("、", site.Benefits) : "无"), smallFont);
        }
        DetailText = string.Join("\n", paragraphs.Select(p => p.Text));
        DestinationButton.Visible = false;
        DestinationButton.Enabled = site != null && site.Id != World.CurrentId && site.Id != DestinationId;
        DestinationButton.Text = site?.Id == World.CurrentId ? "当前位置" : site?.Id == DestinationId ? "已标记目的地" : "标记目的地";
        LayoutDetails(); content.ResumeLayout();
    }

    private void LayoutDetails()
    {
        if (content == null) return;
        int offset = DetailScrollOffset, width = Math.Max(1, detail.ClientSize.Width - SystemInformation.VerticalScrollBarWidth - Unit(8)), y = Unit(12);
        content.Width = width;
        foreach (var p in paragraphs)
        {
            var size = TextRenderer.MeasureText(p.Text, p.Font, new Size(width, int.MaxValue), TextFormatFlags.WordBreak | TextFormatFlags.NoPadding);
            p.Bounds = new Rectangle(0, y, width, Math.Max(p.Font.Height, size.Height)); y += p.Height + Unit(8);
        }
        content.Height = y + Unit(4);
        detail.AutoScrollMinSize = new Size(0, content.Height);
        if (offset > 0) detail.AutoScrollPosition = new Point(0, offset);
        detail.Invalidate();
    }

    internal void Capture(string path)
    {
        using var result = new Bitmap(Math.Max(1, Width), Math.Max(1, Height));
        DrawToBitmap(result, ClientRectangle);
        using var canvas = Surface.CaptureFrame();
        using var graphics = Graphics.FromImage(result);
        graphics.DrawImageUnscaled(canvas, Surface.Location);
        foreach (Control overlay in Surface.Controls)
        {
            if (!overlay.Visible || overlay.Width < 1 || overlay.Height < 1) continue;
            using var bitmap = new Bitmap(overlay.Width, overlay.Height); overlay.DrawToBitmap(bitmap, overlay.ClientRectangle);
            var saved = graphics.Save();
            graphics.TranslateTransform(Surface.Left + overlay.Left, Surface.Top + overlay.Top);
            if (overlay.Region != null) graphics.SetClip(overlay.Region, CombineMode.Intersect);
            graphics.DrawImageUnscaled(bitmap, 0, 0); graphics.Restore(saved);
        }
        result.Save(path, ImageFormat.Png);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { DestinationButton.Dispose(); headingFont.Dispose(); bodyFont.Dispose(); smallFont.Dispose(); }
        base.Dispose(disposing);
    }

    internal static Color Blend(Color a, Color b, float t) => Color.FromArgb((int)(a.R + (b.R - a.R) * t), (int)(a.G + (b.G - a.G) * t), (int)(a.B + (b.B - a.B) * t));
}

internal sealed class NativeMapSurface : UserControl
{
    private readonly NativeSceneWorld world;
    private readonly Action<string> failed;
    private NativeMapGl? renderer;
    private DisplayTheme theme;
    private readonly Dictionary<int, MapCaption> labels = new();
    private readonly MapCaption currentTag, destinationTag;
    private readonly MapInstrument compass, scale;
    private Point? start;
    private Point? rightStart;
    private int? downSite;
    private bool rightDrawing;
    private float startYaw, startElevation;
    private bool dragged;
    private bool failureSent;
    internal NativeMapCamera Camera { get; } = new();
    internal NativeMapRoute Route { get; }
    internal bool Ready => renderer != null && !IsDisposed;
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    internal int ActiveSiteId { get; set; }
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    internal int? DestinationId { get; set; }
    internal int? HoveredId { get; private set; }
    internal long GeometryBytes => renderer?.GeometryBytes ?? 0;
    internal event Action<int?>? HoverChanged;
    internal event Action<int>? Selected;
    internal event Action? RouteChanged;
    private int Unit(float value) => Math.Max(1, (int)Math.Round(value * DeviceDpi / 96f));
    private Color FocusColor => ColorSafety.Selection(theme);

    internal NativeMapSurface(NativeSceneWorld world, DisplayTheme theme, Action<string> failed)
    {
        this.world = world; this.theme = theme.Safe(); this.failed = failed; Route = new NativeMapRoute(world);
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.Opaque, true);
        SetStyle(ControlStyles.OptimizedDoubleBuffer, false);
        TabStop = true; Cursor = Cursors.SizeAll;
        foreach (var site in world.Sites)
        {
            var label = new MapCaption(SiteTitle(site));
            label.MouseEnter += (_, _) => SetHover(site.Id);
            label.MouseLeave += (_, _) => SetHover(null);
            label.Click += (_, _) => { if (!ConfirmRoute()) Selected?.Invoke(site.Id); };
            label.MouseDown += (_, e) => ForwardMouseDown(label, e, site.Id);
            Controls.Add(label); labels.Add(site.Id, label);
        }
        currentTag = new MapCaption("") { Marker = true, Enabled = false };
        destinationTag = new MapCaption("") { Marker = true, Enabled = false };
        scale = new MapInstrument(this, false); compass = new MapInstrument(this, true);
        Controls.Add(currentTag); Controls.Add(destinationTag); Controls.Add(scale); Controls.Add(compass);
        scale.MouseDown += (_, e) => ForwardMouseDown(scale, e, null);
        compass.MouseDown += (_, e) => ForwardMouseDown(compass, e, null);
        UpdateTheme(theme);
    }

    protected override CreateParams CreateParams { get { var p = base.CreateParams; p.ClassStyle |= 0x20; return p; } }
    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        try { renderer = new NativeMapGl(Handle, world); }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            if (!failureSent) { failureSent = true; BeginInvoke(() => { if (!IsDisposed) failed("原生三维绘图暂不可用，已切换兼容地图（" + error.GetType().Name + "）。"); }); }
        }
    }
    protected override void OnHandleDestroyed(EventArgs e) { renderer?.Dispose(); renderer = null; base.OnHandleDestroyed(e); }
    protected override void Dispose(bool disposing)
    {
        // Release the rendering context while its native HWND/HDC are still valid.
        if (disposing) { renderer?.Dispose(); renderer = null; }
        base.Dispose(disposing);
    }
    protected override void OnPaintBackground(PaintEventArgs e) { }
    protected override void OnPaint(PaintEventArgs e)
    {
        if (renderer == null) { e.Graphics.Clear(theme.BackgroundColor); return; }
        try
        {
            renderer.Render(Width, Height, theme, Camera, HoveredId ?? ActiveSiteId,
                world.CurrentMarker, world.Sites.FirstOrDefault(s => s.Id == DestinationId), Route);
            PositionOverlays();
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            if (!failureSent) { failureSent = true; BeginInvoke(() => { if (!IsDisposed) failed("原生三维绘图已停止，已切换兼容地图（" + error.GetType().Name + "）。"); }); }
        }
    }
    protected override void OnResize(EventArgs e) { base.OnResize(e); if (Width > 0 && Height > 0) { Camera.Update(Width, Height); Redraw(); } }
    internal void Redraw() { if (!IsDisposed) Invalidate(); }
    internal Bitmap CaptureFrame() => renderer?.Capture(Math.Max(1, Width), Math.Max(1, Height)) ?? new Bitmap(Math.Max(1, Width), Math.Max(1, Height));
    internal string SiteTitle(NativeSceneSite s) => s.Name + Coordinates(s.X, s.Z);
    private string Coordinates(float x, float z) => $"（{Math.Round((x - world.Origin.X) * world.MetersPerUnit)}, {Math.Round((world.Origin.Y - z) * world.MetersPerUnit)}）";
    internal void ClearHover() => SetHover(null);
    private void SetHover(int? id)
    {
        if (HoveredId == id) return;
        HoveredId = id; HoverChanged?.Invoke(id); Redraw();
    }
    internal void UpdateTheme(DisplayTheme value)
    {
        theme = value.Safe(); BackColor = theme.BackgroundColor;
        foreach (Control child in Controls) { child.BackColor = theme.BackgroundColor; child.ForeColor = theme.TextColor; child.Invalidate(); }
        Redraw();
    }

    private void PositionOverlays()
    {
        scale.Bounds = new Rectangle(Unit(22), Math.Max(0, Height - Unit(93)), Unit(125), Unit(78));
        compass.Bounds = new Rectangle(Math.Max(0, Width - Unit(103)), Math.Max(0, Height - Unit(105)), Unit(84), Unit(84));
        var used = new List<Rectangle> { scale.Bounds, compass.Bounds };
        void Tag(MapCaption label, NativeSceneSite? site, string title, Color color, Vector3? routeEnd = null)
        {
            Vector3? anchor = routeEnd is Vector3 end ? end + Vector3.UnitY * 13.25f : site == null ? null : new Vector3(site.X, world.HeightAt(site.X, site.Z) + site.MarkerHeight + 13, site.Z);
            var p = anchor == null ? null : Camera.Project(anchor.Value, Width, Height);
            label.Visible = p != null && p.Value.X >= 0 && p.Value.X <= Width && p.Value.Y >= 0 && p.Value.Y <= Height;
            if (!label.Visible || anchor == null || p == null) return;
            label.Text = title + Coordinates(anchor.Value.X, anchor.Value.Z); label.ForeColor = color;
            var size = label.PreferredSize; int x = (int)p.Value.X + Unit(6); if (x + size.Width > Width - Unit(8)) x = (int)p.Value.X - size.Width - Unit(8);
            int baseY = Math.Clamp((int)p.Value.Y - size.Height / 2, 0, Math.Max(0, Height - size.Height));
            var bounds = new Rectangle(Math.Max(Unit(8), x), baseY, size.Width, size.Height);
            for (int attempt = 1; attempt <= 8 && used.Any(other => other.IntersectsWith(bounds)); attempt++)
            {
                int shift = (attempt % 2 == 1 ? -1 : 1) * ((attempt + 1) / 2) * (size.Height + Unit(6));
                bounds.Y = Math.Clamp(baseY + shift, 0, Math.Max(0, Height - size.Height));
            }
            label.Bounds = bounds;
            used.Add(label.Bounds);
        }
        Tag(currentTag, world.CurrentMarker, "当前", theme.TextColor);
        Tag(destinationTag, world.Sites.FirstOrDefault(s => s.Id == DestinationId), "目的地", FocusColor, Route.EndPoint);
        foreach (var site in world.Sites.OrderByDescending(s => s.Id == world.CurrentId))
        {
            var label = labels[site.Id]; var p = Camera.Project(new Vector3(site.X, site.Y + 1.5f, site.Z), Width, Height);
            if (p == null || p.Value.X < 0 || p.Value.X > Width || p.Value.Y < 0 || p.Value.Y > Height) { label.Visible = false; continue; }
            var size = label.PreferredSize;
            int x = Math.Clamp((int)p.Value.X - size.Width / 2, Unit(8), Math.Max(Unit(8), Width - size.Width - Unit(8)));
            int low = Unit(8), high = Height - size.Height - Unit(8), y = Math.Clamp((int)p.Value.Y + Unit(25) - size.Height / 2, low, Math.Max(low, high));
            Rectangle? target = null;
            for (int i = 0; i < 23; i++)
            {
                int candidate = y + (i == 0 ? 0 : (i % 2 == 1 ? -1 : 1) * ((i + 1) / 2) * (size.Height + Unit(9)));
                if (candidate < low || candidate > high) continue;
                var box = new Rectangle(x, candidate, size.Width, size.Height);
                if (used.Any(r => { r.Inflate(Unit(6), Unit(6)); return r.IntersectsWith(box); })) continue;
                target = box; break;
            }
            label.Visible = target != null;
            if (target is Rectangle bounds) { label.Bounds = bounds; used.Add(bounds); }
            Color color = site.Id == (HoveredId ?? ActiveSiteId) ? FocusColor : theme.TextColor;
            if (label.ForeColor != color) { label.ForeColor = color; label.Invalidate(); }
        }
        scale.Invalidate(); compass.Invalidate();
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (start != null || rightStart != null) return;
        if (e.Button == MouseButtons.Right)
        {
            Focus(); rightStart = e.Location; rightDrawing = false; ClearHover(); Capture = true; return;
        }
        if (e.Button != MouseButtons.Left) return;
        Focus(); downSite = null; start = e.Location; startYaw = Camera.Yaw; startElevation = Camera.Elevation; dragged = false; Capture = true;
    }
    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (rightStart is Point rightOrigin)
        {
            float distance = Vector2.Distance(new Vector2(rightOrigin.X, rightOrigin.Y), new Vector2(e.X, e.Y));
            if (!rightDrawing && distance > Unit(4) && GroundAtPointer(e.Location) != null && Route.Begin())
            {
                rightDrawing = true; DestinationId = null;
                if (GroundAtPointer(rightOrigin) is Vector3 first) Route.Append(first.X, first.Z);
            }
            if (rightDrawing && GroundAtPointer(e.Location) is Vector3 ground) Route.Append(ground.X, ground.Z);
            if (rightDrawing) { RouteChanged?.Invoke(); Redraw(); }
            return;
        }
        if (start is Point origin)
        {
            int dx = e.X - origin.X, dy = e.Y - origin.Y; if (dx * dx + dy * dy > 16) dragged = true;
            float dpi = DeviceDpi / 96f; Camera.Yaw = startYaw - dx / dpi * .005f; Camera.Elevation = Math.Clamp(startElevation + dy / dpi * .004f, .38f, 1.2f);
            SetHover(null); Redraw();
        }
        else SetHover(Pick(e.Location));
    }
    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.Button == MouseButtons.Right && rightStart != null)
        {
            if (rightDrawing)
            {
                if (GroundAtPointer(e.Location) is Vector3 ground) Route.Append(ground.X, ground.Z);
                Route.Finish();
            }
            else Route.Clear();
            rightStart = null; rightDrawing = false; Capture = false; RouteChanged?.Invoke(); Redraw(); return;
        }
        if (e.Button != MouseButtons.Left || start == null) return;
        bool clicked = !dragged; int? site = downSite; start = null; downSite = null; Capture = false;
        if (clicked && !ConfirmRoute() && (site ?? Pick(e.Location)) is int id) Selected?.Invoke(id);
        Redraw();
    }
    protected override void OnMouseCaptureChanged(EventArgs e)
    {
        base.OnMouseCaptureChanged(e);
        if (Capture || Disposing || IsDisposed) return;
        start = null; downSite = null; rightStart = null;
        if (rightDrawing) { rightDrawing = false; Route.Finish(); RouteChanged?.Invoke(); Redraw(); }
    }
    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); if (start == null && rightStart == null) SetHover(null); }
    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        if (rightStart == null) { Camera.Distance = Math.Clamp(Camera.Distance * MathF.Exp(-e.Delta * .001f), 90, 215); SetHover(null); Redraw(); }
        if (e is HandledMouseEventArgs handled) handled.Handled = true;
    }

    private void ForwardMouseDown(Control child, MouseEventArgs e, int? site)
    {
        if (e.Button is not (MouseButtons.Left or MouseButtons.Right)) return;
        var point = PointToClient(child.PointToScreen(e.Location));
        OnMouseDown(new MouseEventArgs(e.Button, e.Clicks, point.X, point.Y, e.Delta));
        if (e.Button == MouseButtons.Left) downSite = site;
    }
    protected override void WndProc(ref Message m)
    {
        if (m.Msg == 0x007B) return; // The right button belongs to route gestures, not context menus.
        base.WndProc(ref m);
    }
    internal bool ConfirmRoute()
    {
        if (!Route.Confirm()) return false;
        RouteChanged?.Invoke(); Redraw(); return true;
    }
    internal void ClearRoute()
    {
        if (!Route.Clear()) return;
        RouteChanged?.Invoke(); Redraw();
    }

    internal Vector3? GroundAtPointer(Point point)
    {
        if (!Ready || point.X < 0 || point.X > Width || point.Y < 0 || point.Y > Height) return null;
        Camera.Update(Width, Height); var ray = Camera.Ray(point.X, point.Y, Width, Height);
        float nearest = float.PositiveInfinity; var data = world.Terrain;
        for (int i = 0; i + 29 < data.Length; i += 30)
        {
            var a = new Vector3(data[i], data[i + 1], data[i + 2]);
            var b = new Vector3(data[i + 10], data[i + 11], data[i + 12]);
            var c = new Vector3(data[i + 20], data[i + 21], data[i + 22]);
            var e1 = b - a; var e2 = c - a; var h = Vector3.Cross(ray.Direction, e2); float det = Vector3.Dot(e1, h);
            if (Math.Abs(det) < .000001f) continue;
            float inv = 1 / det; var s = ray.Origin - a; float u = inv * Vector3.Dot(s, h); if (u < 0 || u > 1) continue;
            var q = Vector3.Cross(s, e1); float v = inv * Vector3.Dot(ray.Direction, q); if (v < 0 || u + v > 1) continue;
            float distance = inv * Vector3.Dot(e2, q); if (distance > 0 && distance < nearest) nearest = distance;
        }
        return float.IsFinite(nearest) ? ray.Origin + ray.Direction * nearest : null;
    }

    internal int? Pick(Point point)
    {
        if (!Ready || Width < 1 || Height < 1) return null;
        Camera.Update(Width, Height); var ray = Camera.Ray(point.X, point.Y, Width, Height);
        float nearest = float.PositiveInfinity; int owner = 0;
        foreach (var data in new[] { world.Terrain, world.River, world.Routes, world.Solids })
        {
            for (int i = 0; i + 29 < data.Length; i += 30)
            {
                var a = new Vector3(data[i], data[i + 1], data[i + 2]);
                var b = new Vector3(data[i + 10], data[i + 11], data[i + 12]);
                var c = new Vector3(data[i + 20], data[i + 21], data[i + 22]);
                var e1 = b - a; var e2 = c - a; var h = Vector3.Cross(ray.Direction, e2); float det = Vector3.Dot(e1, h);
                if (Math.Abs(det) < .000001f) continue;
                float inv = 1 / det; var s = ray.Origin - a; float u = inv * Vector3.Dot(s, h); if (u < 0 || u > 1) continue;
                var q = Vector3.Cross(s, e1); float v = inv * Vector3.Dot(ray.Direction, q); if (v < 0 || u + v > 1) continue;
                float distance = inv * Vector3.Dot(e2, q); if (distance <= 0 || distance >= nearest) continue;
                nearest = distance; owner = (int)data[i + 6];
            }
        }
        if (world.RenderIdToSiteId.TryGetValue(owner, out int id)) return id;
        var floor = Camera.FloorPoint(point.X, point.Y, Width, Height, world);
        if (floor == null) return null;
        foreach (var site in world.Sites)
        {
            bool inside = false; var poly = site.Outline;
            for (int i = 0, j = poly.Length - 1; i < poly.Length; j = i++)
            {
                var a = poly[i]; var b = poly[j];
                if ((a.Y > floor.Value.Z) != (b.Y > floor.Value.Z) && floor.Value.X < (b.X - a.X) * (floor.Value.Z - a.Y) / (b.Y - a.Y) + a.X) inside = !inside;
            }
            if (inside) return site.Id;
        }
        return null;
    }

    private sealed class MapCaption : Control
    {
        internal bool Marker;
        internal MapCaption(string text) { Text = text; Font = new Font("Microsoft YaHei UI", 9); Cursor = Cursors.Hand; SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true); }
        public override Size GetPreferredSize(Size proposedSize) { var s = TextRenderer.MeasureText(Text, Font, Size.Empty, TextFormatFlags.NoPadding); return new Size(s.Width + 14, s.Height + 8); }
        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(BackColor); TextRenderer.DrawText(e.Graphics, Text, Font, ClientRectangle, ForeColor, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            using var pen = new Pen(ForeColor, Marker ? 2 : 1);
            if (Marker) e.Graphics.DrawLine(pen, 0, 0, 0, Height); else e.Graphics.DrawLine(pen, 0, Height - 1, Width, Height - 1);
        }
        protected override void Dispose(bool disposing) { if (disposing) Font.Dispose(); base.Dispose(disposing); }
    }

    private sealed class MapInstrument(NativeMapSurface surface, bool compass) : Control
    {
        private readonly Font font = new("Microsoft YaHei UI", 8.25f);
        private string regionKey = "";
        private void SetShape(string key, Region region)
        {
            if (key == regionKey) { region.Dispose(); return; }
            regionKey = key; var old = Region; Region = region; old?.Dispose();
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics; g.Clear(BackColor); g.SmoothingMode = SmoothingMode.AntiAlias;
            var color = surface.theme.TextColor;
            if (compass)
            {
                float cx = Width / 2f, cy = Height / 2f, radius = Math.Min(Width, Height) * .28f;
                var camera = surface.Camera; var t = camera.Target; float y = surface.world.HeightAt(t.X, t.Z);
                var center = camera.Project(new Vector3(t.X, y, t.Z), surface.Width, surface.Height);
                var north = camera.Project(new Vector3(t.X, y, t.Z - 5), surface.Width, surface.Height);
                float angle = center == null || north == null ? 0 : MathF.Atan2(north.Value.X - center.Value.X, center.Value.Y - north.Value.Y);
                using var path = new GraphicsPath(); path.AddEllipse(cx - radius - 2, cy - radius - 2, radius * 2 + 4, radius * 2 + 4);
                var region = new Region(path);
                using var pen = new Pen(NativeSceneMapView.Blend(BackColor, color, .4f)); g.DrawEllipse(pen, cx - radius, cy - radius, radius * 2, radius * 2);
                var saved = g.Save(); g.TranslateTransform(cx, cy); g.RotateTransform(angle * 180 / MathF.PI);
                using var focus = new SolidBrush(surface.FocusColor); using var south = new SolidBrush(NativeSceneMapView.Blend(BackColor, color, .55f));
                g.FillPolygon(focus, [new PointF(0, -radius), new PointF(-radius * .23f, radius * .06f), new PointF(0, -radius * .12f), new PointF(radius * .23f, radius * .06f)]);
                g.FillPolygon(south, [new PointF(0, radius), new PointF(-radius * .23f, -radius * .06f), new PointF(0, radius * .12f), new PointF(radius * .23f, -radius * .06f)]); g.Restore(saved);
                string[] labels = ["北", "东", "南", "西"];
                for (int i = 0; i < 4; i++) { float a = angle + i * MathF.PI / 2; var rect = new Rectangle((int)(cx + Math.Sin(a) * radius * 1.48) - 15, (int)(cy - Math.Cos(a) * radius * 1.48) - 10, 30, 20); region.Union(rect); TextRenderer.DrawText(g, labels[i], font, rect, color, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter); }
                SetShape(Width + ":" + Height + ":" + (int)(angle * 1000), region);
            }
            else
            {
                var camera = surface.Camera; float cx = surface.Width / 2f, cy = surface.Height * .52f;
                var a = camera.FloorPoint(cx - 8, cy, surface.Width, surface.Height, surface.world); var b = camera.FloorPoint(cx + 8, cy, surface.Width, surface.Height, surface.world);
                if (a == null || b == null) return;
                float perPixel = new Vector2(a.Value.X - b.Value.X, a.Value.Z - b.Value.Z).Length() * surface.world.MetersPerUnit / 16;
                if (perPixel <= 0) return;
                float ideal = perPixel * surface.Unit(100), power = MathF.Pow(10, MathF.Floor(MathF.Log10(ideal))), fraction = ideal / power, metres = (fraction >= 5 ? 5 : fraction >= 2 ? 2 : 1) * power;
                int length = Math.Clamp((int)(metres / perPixel), 8, Width - 2), top = surface.Unit(22), bar = surface.Unit(4);
                var region = new Region(Rectangle.Empty); region.MakeEmpty();
                region.Union(new Rectangle(0, 0, Width, surface.Unit(18)));
                region.Union(new Rectangle(0, top, Width, bar + surface.Unit(22)));
                region.Union(new Rectangle(0, Height - surface.Unit(20), Width, surface.Unit(20)));
                SetShape(Width + ":" + Height, region);
                TextRenderer.DrawText(g, "中心示意比例尺", font, new Point(0, 0), color);
                using var brush = new SolidBrush(color); using var pen = new Pen(color); g.FillRectangle(brush, 0, top, length / 2, bar); g.DrawRectangle(pen, 0, top, length, bar);
                TextRenderer.DrawText(g, "0", font, new Point(0, top + bar + 3), color);
                TextRenderer.DrawText(g, metres >= 1000 ? metres / 1000 + " 千米" : metres + " 米", font, new Rectangle(0, top + bar + 3, length + 8, surface.Unit(19)), color, TextFormatFlags.Right);
                TextRenderer.DrawText(g, "拖动旋转 · 滚轮缩放", font, new Point(0, Height - surface.Unit(20)), color);
            }
        }
        protected override void Dispose(bool disposing) { if (disposing) font.Dispose(); base.Dispose(disposing); }
    }
}
