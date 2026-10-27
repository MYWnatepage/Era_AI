#nullable enable
using System;
using System.Drawing;
using System.Windows.Forms;

namespace MinorShift.Emuera.NativeDisplay;

internal sealed class NarrativeDisplayEditor(NarrativeDisplaySettings initial, Func<NarrativeDisplaySettings, bool, string> change, Action finish)
{
    internal NarrativeDisplaySettings Settings { get; private set; } = initial;
    internal string Message { get; private set; } = "";
    internal Rectangle Track { get; private set; }
    internal Rectangle GradualBounds { get; private set; }
    internal Rectangle InstantBounds { get; private set; }
    internal Rectangle NonStreamingBounds { get; private set; }
    internal Rectangle StreamingBounds { get; private set; }
    internal bool StreamingPreview { get; private set; }
    private Rectangle back;
    private bool dragging;
    private Point? mouse;
    private void Apply(NarrativeDisplaySettings value, bool save = true)
    {
        Message = change(value, save);
        if (Message.Length == 0) Settings = value;
    }
    internal void Reception(bool streaming) { dragging = false; StreamingPreview = streaming; Message = ""; }
    internal void Mode(bool gradual) { if (!StreamingPreview) Apply(Settings with { Gradual = gradual }); }
    internal void Speed(int speed, bool save = true)
    {
        if (!StreamingPreview && Settings.Gradual) Apply(Settings with { CharactersPerSecond = Math.Clamp(speed, 1, 120) }, save);
    }
    internal void Submit(string input)
    {
        if (input.Trim() == "0") { Close(); return; }
        if (StreamingPreview) return;
        if (!Settings.Gradual) { Message = "选择逐字显示后可以调整速度。"; return; }
        if (!int.TryParse(input.Trim(), out int value) || value is < 1 or > 120)
            Message = "请输入1～120的整数；输入0返回。";
        else Speed(value);
    }
    internal void Close()
    {
        Apply(Settings);
        if (Message.Length == 0) finish();
    }
    internal void MouseDown(Point point)
    {
        if (back.Contains(point)) { Close(); return; }
        if (NonStreamingBounds.Contains(point)) { Reception(false); return; }
        if (StreamingBounds.Contains(point)) { Reception(true); return; }
        if (StreamingPreview) return;
        if (GradualBounds.Contains(point)) { Mode(true); return; }
        if (InstantBounds.Contains(point)) { Mode(false); return; }
        if (!Settings.Gradual || !Rectangle.Inflate(Track, 8, 0).Contains(point)) return;
        dragging = true; Move(point);
    }
    internal void Move(Point point)
    {
        mouse = point;
        if (dragging) Speed(1 + (int)Math.Round((point.X - Track.Left) * 119d / Math.Max(1, Track.Width)), false);
    }
    internal void MouseUp() { if (dragging) { dragging = false; Apply(Settings); } }
    internal void MouseLeave() => mouse = null;
    internal void Draw(Graphics graphics, Rectangle area, Font font, DisplayTheme theme, int lineHeight)
    {
        graphics.Clear(theme.BackgroundColor);
        int rows = Message.Length > 0 ? 11 : 10;
        int line = Math.Max(1, Math.Min(lineHeight, area.Height / rows));
        using var fitted = new Font(font.FontFamily, Math.Max(1, font.Size * Math.Min(1f, (float)line / lineHeight)), font.Style, GraphicsUnit.Pixel);
        int x = Math.Max(8, line / 2), y = Math.Max(0, area.Height - line * rows), width = Math.Max(1, area.Width - x * 2);
        var flags = TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix;
        void Text(string text, Point at, Color color) => TextRenderer.DrawText(graphics, text, fitted, at, color, theme.BackgroundColor, flags);
        void Row(string text) { Text(text, new(x, y), theme.TextColor); y += line; }
        void Rule() { using var pen = new Pen(theme.TextColor) { DashStyle = System.Drawing.Drawing2D.DashStyle.Dash }; graphics.DrawLine(pen, x, y + line / 2, x + width, y + line / 2); y += line; }
        Rectangle Choice(string text, int left, bool selected)
        {
            int w = TextRenderer.MeasureText(text, fitted, new Size(int.MaxValue, int.MaxValue), flags).Width;
            var rect = new Rectangle(left, y, w, line);
            Text(text, rect.Location, selected || mouse is Point p && rect.Contains(p) ? ColorSafety.Selection(theme) : theme.TextColor);
            return rect;
        }
        Rule(); Row("剧情显示"); Rule();
        Text("回复接收：", new(x, y), theme.TextColor);
        int receptionWidth = TextRenderer.MeasureText("回复接收：", fitted, new Size(int.MaxValue, int.MaxValue), flags).Width;
        NonStreamingBounds = Choice("[非流式]", x + receptionWidth, !StreamingPreview);
        StreamingBounds = Choice("[流式]", NonStreamingBounds.Right + line, StreamingPreview); y += line;
        if (StreamingPreview)
        {
            GradualBounds = InstantBounds = Track = Rectangle.Empty;
            Rule(); Row("（未完成）");
        }
        else
        {
        GradualBounds = Choice("[逐字显示]", x, Settings.Gradual);
        InstantBounds = Choice("[一起显示]", GradualBounds.Right + line, !Settings.Gradual); y += line;
        int label = Math.Min(140, line * 5);
        Text("显示速度", new(x, y), theme.TextColor);
        Track = new(x + label, y, Math.Max(1, Math.Min(520, width - label - line * 6)), line);
        var trackColor = Settings.Gradual ? theme.TextColor : Color.FromArgb((theme.TextColor.R + theme.BackgroundColor.R) / 2,
            (theme.TextColor.G + theme.BackgroundColor.G) / 2, (theme.TextColor.B + theme.BackgroundColor.B) / 2);
        using (var pen = new Pen(trackColor, 2)) graphics.DrawLine(pen, Track.Left, y + line / 2, Track.Right, y + line / 2);
        using (var brush = new SolidBrush(Settings.Gradual ? ColorSafety.Selection(theme) : trackColor))
            graphics.FillRectangle(brush, Track.Left + Track.Width * (Settings.CharactersPerSecond - 1) / 119 - 3, y + line / 4, 7, Math.Max(1, line / 2));
        Text($"{Settings.CharactersPerSecond} 字/秒", new(Track.Right + line / 2, y), theme.TextColor); y += line;
        Rule(); Row(Settings.Gradual ? "拖动滑块或↑↓调整速度；输入1～120回车应用。" : "一起显示完整剧情，切换逐字显示后可调节速度。");
        Row("逐字显示时，按回车或点击剧情可立即显示完整内容。");
        if (Message.Length > 0) Row(Message);
        }
        back = new(x, y, Math.Min(width, TextRenderer.MeasureText("[0] 返回设置", fitted, new Size(int.MaxValue, int.MaxValue), flags).Width), line);
        Text("[0] 返回设置", back.Location, mouse is Point point && back.Contains(point) ? ColorSafety.Selection(theme) : theme.TextColor);
    }
}
