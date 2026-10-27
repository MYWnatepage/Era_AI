using System;
using System.Drawing;
using System.Windows.Forms;

namespace MinorShift.Emuera.NativeDisplay;

internal sealed class MusicVolumeEditor(int initial, Func<int, bool, string> change, Action finish)
{
    public int Value { get; private set; } = initial;
    public string Message { get; private set; } = "";
    public Rectangle Track { get; private set; }
    private Rectangle back;
    private bool dragging;
    private Point? mousePosition;
    public void Set(int value, bool save = true)
    {
        value = Math.Clamp(value, 0, 100);
        Message = change(value, save);
        if (Message.Length == 0) Value = value;
    }
    public void Submit(string input)
    {
        if (!int.TryParse(input.Trim().TrimEnd('%', '％'), out int value) || value is < 0 or > 100)
            Message = "请输入 0～100 的整数，回车应用；Esc 返回。";
        else Set(value);
    }
    public void Close()
    {
        Set(Value);
        if (Message.Length == 0) finish();
    }
    public void MouseDown(Point point)
    {
        if (back.Contains(point)) { Close(); return; }
        if (!Rectangle.Inflate(Track, 8, 0).Contains(point)) return;
        dragging = true;
        Move(point);
    }
    public void Move(Point point)
    {
        mousePosition = point;
        if (dragging) Set((int)Math.Round((point.X - Track.Left) * 100d / Math.Max(1, Track.Width)), false);
    }
    public void MouseLeave() => mousePosition = null;
    public void MouseUp() { if (dragging) { dragging = false; Set(Value); } }
    public void Draw(Graphics graphics, Rectangle area, Font font, DisplayTheme theme, int lineHeight)
    {
        graphics.Clear(theme.BackgroundColor);
        int rowCount = Message.Length > 0 ? 10 : 9;
        int line = Math.Max(1, Math.Min(lineHeight, area.Height / rowCount));
        using var fitted = new Font(font.FontFamily, Math.Max(1, font.Size * Math.Min(1f, (float)line / lineHeight)), font.Style, GraphicsUnit.Pixel);
        int x = Math.Max(8, line / 2), y = Math.Max(0, area.Height - line * rowCount);
        int width = Math.Max(1, area.Width - x * 2);
        void Row(string text)
        {
            TextRenderer.DrawText(graphics, text, fitted, new Point(x, y), theme.TextColor,
                theme.BackgroundColor, TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
            y += line;
        }
        void Rule()
        {
            using var pen = new Pen(theme.TextColor) { DashStyle = System.Drawing.Drawing2D.DashStyle.Dash };
            graphics.DrawLine(pen, x, y + line / 2, x + width, y + line / 2);
            y += line;
        }
        Rule(); Row("音乐音量"); Rule();
        // Match the compact RGB rows: label, fixed-length track, value in one line.
        int labelWidth = Math.Min(140, line * 5);
        TextRenderer.DrawText(graphics, "音乐音量", fitted, new Point(x, y), theme.TextColor,
            theme.BackgroundColor, TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
        int trackWidth = Math.Max(1, Math.Min(520, width - labelWidth - line * 5));
        Track = new Rectangle(x + labelWidth, y, trackWidth, line);
        using (var pen = new Pen(theme.TextColor, 2))
            graphics.DrawLine(pen, Track.Left, y + line / 2, Track.Right, y + line / 2);
        using (var brush = new SolidBrush(ColorSafety.Selection(theme)))
            graphics.FillRectangle(brush, Track.Left + Track.Width * Value / 100 - 3, y + line / 4, 7, Math.Max(1, line / 2));
        TextRenderer.DrawText(graphics, $"{Value}%", fitted, new Point(Track.Right + line / 2, y), theme.TextColor,
            theme.BackgroundColor, TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
        y += line;
        Rule();
        Row("音量说明：范围 0～100%；0% 静音，保留背景音乐开关状态。");
        Row("修改建议：先用 50%，再根据设备音量调整。");
        Row("使用说明：拖动滑块或 ↑↓ 调整10%；输入0～100回车应用；Esc或点击返回。");
        if (Message.Length > 0) Row(Message);
        const string backLabel = "[Esc] 返回设置";
        back = new Rectangle(x, y, Math.Min(width, TextRenderer.MeasureText(backLabel, fitted,
            new Size(int.MaxValue, int.MaxValue), TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix).Width), line);
        TextRenderer.DrawText(graphics, backLabel, fitted, new Point(x, y),
            mousePosition is Point point && back.Contains(point) ? ColorSafety.Selection(theme) : theme.TextColor,
            theme.BackgroundColor, TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
    }
}
