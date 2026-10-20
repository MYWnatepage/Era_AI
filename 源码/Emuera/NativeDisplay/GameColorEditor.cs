using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace MinorShift.Emuera.NativeDisplay;

// 使用原来的游戏画布和输入栏，不创建窗体或子面板。
internal sealed class GameColorEditor
{
    public DisplayTheme Candidate { get; private set; }
    public int Target { get; private set; }
    public int Channel { get; private set; }
    public bool EnteringValue { get; private set; }
    public string Message { get; private set; } = "";
    private readonly Action<DisplayTheme> preview, save;
    private readonly Action<bool> finish;
    private readonly Rectangle[] tracks = new Rectangle[3];
    private readonly List<(Rectangle Bounds, int Command)> links = new();
    private bool dragging, completed;
    private Point? mousePosition;
    private static readonly string[] Names = ["红 R", "绿 G", "蓝 B"];

    public GameColorEditor(DisplayTheme theme, int target, Action<DisplayTheme> preview,
        Action<DisplayTheme> save, Action<bool> finish)
    {
        Candidate = theme;
        Target = Math.Clamp(target, 0, 2);
        this.preview = preview;
        this.save = save;
        this.finish = finish;
    }
    public int Value(int channel) => ((Target == 2 ? (Candidate.FocusRgb == -1 ? ColorSafety.Pack(ColorSafety.Selection(Candidate)) : Candidate.FocusRgb) : Target == 1 ? Candidate.BackgroundRgb : Candidate.TextRgb) >> ((2 - channel) * 8)) & 255;
    public void Change(int channel, int value)
    {
        Channel = channel;
        int shift = (2 - channel) * 8;
        int rgb = Target == 2 ? (Candidate.FocusRgb == -1 ? ColorSafety.Pack(ColorSafety.Selection(Candidate)) : Candidate.FocusRgb) : Target == 1 ? Candidate.BackgroundRgb : Candidate.TextRgb;
        rgb = (rgb & ~(255 << shift)) | (Math.Clamp(value, 0, 255) << shift);
        Candidate = Target == 2 ? Candidate with { FocusRgb = rgb } : Target == 1 ? Candidate with { BackgroundRgb = rgb } : Candidate with { TextRgb = rgb };
        Changed();
    }
    private void Changed()
    {
        Message = "";
        if (Candidate.IsValid) preview(Candidate);
    }
    public void PreviewInput(string input)
    {
        if (EnteringValue && int.TryParse(input.Trim(), out int value) && value is >= 0 and <= 255)
            Change(Channel, value);
    }
    public void Submit(string input)
    {
        if (completed) return;
        if (!int.TryParse(input.Trim(), out int number)) { Message = "请输入菜单编号或 0～255 的 RGB 数值。"; return; }
        if (EnteringValue)
        {
            if (number is < 0 or > 255) { Message = "数值范围为 0～255，请重新输入。"; return; }
            Change(Channel, number);
            EnteringValue = false;
        }
        else Command(number);
    }
    public void Command(int command)
    {
        if (completed) return;
        EnteringValue = false;
        switch (command)
        {
            case 0: Finish(false); break;
            case 1: case 2: case 3: Channel = command - 1; EnteringValue = true; Message = ""; break;
            case 4: Target = (Target + 1) % 3; Message = ""; break;
            case 5: Candidate = new(); Changed(); break;
            case 6: Candidate = new(0xDCDCDC, 0x0F0F12); Changed(); break;
            case 7: Candidate = new(0x302B25, 0xF4EBDD); Changed(); break;
            case 8:
                if (!Candidate.IsValid) { Message = "配色不够清晰，请拉开文字、背景与焦点的颜色差异。"; break; }
                try { save(Candidate); Finish(true); }
                catch { Message = "保存失败，请检查游戏目录的写入权限，然后重试。"; }
                break;
            default: Message = "输入无效，请选择页面上的菜单编号。"; break;
        }
    }
    private void Finish(bool saved) { completed = true; finish(saved); }
    public void Escape()
    {
        if (EnteringValue) { EnteringValue = false; Message = ""; }
        else Command(0);
    }
    public void Draw(Graphics graphics, Rectangle area, Font font, DisplayTheme visible, int lineHeight)
    {
        graphics.Clear(visible.BackgroundColor);
        links.Clear();
        int rowCount = Message.Length > 0 ? 16 : 15;
        int line = Math.Max(1, Math.Min(lineHeight, (area.Height - 4) / rowCount));
        using var fittedFont = new Font(font.FontFamily, Math.Max(1, font.Size * Math.Min(1f, (float)line / Math.Max(1, lineHeight))), font.Style, GraphicsUnit.Pixel);
        font = fittedFont;
        int x = Math.Max(8, line / 2);
        int y = Math.Max(0, area.Height - line * rowCount);
        int width = Math.Max(1, area.Width - x * 2);
        Color text = visible.TextColor;
        Color accent = ColorSafety.Selection(visible);
        void Text(string value, int left, int top, Color color) => TextRenderer.DrawText(graphics, value, font,
            new Point(left, top), color, visible.BackgroundColor, TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
        void Row(string value) { Text(value, x, y, text); y += line; }
        void Rule() { using var pen = new Pen(text) { DashStyle = System.Drawing.Drawing2D.DashStyle.Dash }; graphics.DrawLine(pen, x, y + line / 2, x + width, y + line / 2); y += line; }
        void Link(string value, int command)
        {
            var bounds = new Rectangle(x, y, Math.Min(width, TextRenderer.MeasureText(value, font).Width), line);
            Text(value, x, y, IsHovered(bounds) ? accent : text);
            links.Add((bounds, command));
            y += line;
        }
        Rule();
        Row(new[] { "文本颜色设置", "背景颜色设置", "焦点颜色设置" }[Target]);
        Rule();
        var focus = Candidate.FocusRgb == -1 ? ColorSafety.Selection(Candidate) : ColorSafety.FocusColor(Candidate);
        string colors = $"文字：RGB({Candidate.TextColor.R}, {Candidate.TextColor.G}, {Candidate.TextColor.B})    背景：RGB({Candidate.BackgroundColor.R}, {Candidate.BackgroundColor.G}, {Candidate.BackgroundColor.B})    焦点：RGB({focus.R}, {focus.G}, {focus.B})";
        int colorsWidth = TextRenderer.MeasureText(colors, font, new Size(int.MaxValue, int.MaxValue), TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix).Width;
        using (var summaryFont = new Font(font.FontFamily, Math.Max(1, font.Size * Math.Min(1f, (float)width / Math.Max(1, colorsWidth))), font.Style, GraphicsUnit.Pixel))
            TextRenderer.DrawText(graphics, colors, summaryFont, new Point(x, y), text, visible.BackgroundColor, TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
        y += line;
        for (int i = 0; i < 3; i++)
        {
            string label = $"[{i + 1}] {Names[i]}";
            int labelWidth = Math.Min(140, line * 5);
            var labelBounds = new Rectangle(x, y, Math.Min(labelWidth, TextRenderer.MeasureText(label, font).Width), line);
            Text(label, x, y, IsHovered(labelBounds) || (EnteringValue && Channel == i) ? accent : text);
            links.Add((labelBounds, i + 1));
            int trackWidth = Math.Max(50, Math.Min(520, width - labelWidth - line * 5));
            tracks[i] = new Rectangle(x + labelWidth, y, trackWidth, line);
            using var pen = new Pen(text, 2);
            graphics.DrawLine(pen, tracks[i].Left, y + line / 2, tracks[i].Right, y + line / 2);
            int position = tracks[i].Left + Value(i) * trackWidth / 255;
            using var brush = new SolidBrush(accent);
            graphics.FillRectangle(brush, position - 3, y + line / 4, 7, line / 2);
            Text($"{Value(i),3}", tracks[i].Right + line / 2, y, text);
            y += line;
        }
        Rule();
        Link(new[] { "[4] 切换到背景颜色", "[4] 切换到焦点颜色", "[4] 切换到文本颜色" }[Target], 4);
        Link("[5] 恢复默认", 5);
        Link("[6] 柔和深色", 6);
        Link("[7] 暖纸浅色", 7);
        Link(Candidate.IsValid ? "[8] 保存并返回设置" : "[8] 保存（颜色太接近，暂不可用）", 8);
        Link("[0] 取消修改，返回设置", 0);
        Row(EnteringValue ? $"请输入{Names[Channel]}数值（0～255），回车应用；Esc 取消输入。" : "选择菜单编号并回车，或直接点击菜单；Esc 取消并返回。");
        if (Message.Length > 0) Row(Message);
    }
    public void MouseDown(Point location)
    {
        for (int i = 0; i < tracks.Length; i++)
            if (tracks[i].Contains(location)) { Channel = i; dragging = true; EnteringValue = false; Drag(location); return; }
        foreach (var link in links)
            if (link.Bounds.Contains(location)) { Command(link.Command); return; }
    }
    public void Drag(Point location)
    {
        if (dragging && tracks[Channel].Width > 0)
            Change(Channel, (int)Math.Round((location.X - tracks[Channel].Left) * 255.0 / tracks[Channel].Width));
    }
    private bool IsHovered(Rectangle bounds) => mousePosition is Point position && bounds.Contains(position);
    public void MoveMouse(Point location)
    {
        mousePosition = location;
        Drag(location);
    }
    public void MouseLeave() => mousePosition = null;
    public void MouseUp() => dragging = false;
    public void Wheel(Point location, int delta)
    {
        for (int i = 0; i < tracks.Length; i++)
            if (tracks[i].Contains(location)) Channel = i;
        if (delta != 0) Change(Channel, Value(Channel) + Math.Sign(delta));
    }
}
