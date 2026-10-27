#nullable enable
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Linq;
using System.Windows.Forms;
using MinorShift.Emuera.NativeAI;

namespace MinorShift.Emuera.NativeDisplay;

// Draw directly on the existing game canvas, with the usual menu input and Esc return.
internal sealed class UsageChartPage
{
    internal UsagePoint[] Days { get; }
    internal bool Tokens { get; private set; }
    internal int Selected { get; private set; } = 6;
    private readonly Action close;
    internal string ReturnLabel { get; }
    internal string Notice { get; }
    private readonly List<(Rectangle Bounds, int Command)> links = [];
    private Rectangle plot;
    private Point? mouse;
    private bool completed;

    internal UsageChartPage(UsagePoint[] days, Action close, string returnLabel = "返回模型连接设置", string notice = "")
    {
        if (days.Length != 7) throw new ArgumentException("需要连续七天的统计记录。");
        Days = days;
        this.close = close;
        ReturnLabel = returnLabel;
        Notice = notice;
    }

    internal void Command(int number)
    {
        if (completed) return;
        if (number == 0) { completed = true; close(); }
        else if (number is 1 or 2) Tokens = number == 2;
    }
    internal void Submit(string value) { if (int.TryParse(value.Trim(), out int n)) Command(n); }
    internal void Step(int delta) => Selected = Math.Clamp(Selected + delta, 0, 6);
    internal void MouseLeave() => mouse = null;
    internal void Move(Point position)
    {
        mouse = position;
        if (plot.Contains(position)) Selected = Math.Clamp((int)Math.Round((position.X - plot.Left) * 6.0 / plot.Width), 0, 6);
    }
    internal void MouseDown(Point position)
    {
        foreach (var link in links)
            if (link.Bounds.Contains(position)) { Command(link.Command); return; }
        Move(position);
    }
    internal string Details(int index)
    {
        var point = Days[index];
        string date = point.Day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        if (point.Record is not { } day) return date + "：暂无本游戏记录";
        if (day.Requests <= day.MissingUsage) return date + "：请求未返回有效用量，暂无法完整统计。";
        string amount = AccountUsageStore.CostText(day);
        return $"{date}：输入 {day.Input:N0} / 输出 {day.Output:N0} Token；花费 {amount}";
    }
    internal (double Low, double High)? Value(int index)
    {
        var day = Days[index].Record;
        if (day == null) return null;
        if (Tokens) return day.Requests <= day.MissingUsage ? null : ((double)day.Input + day.Output, (double)day.Input + day.Output);
        if (day.MissingUsage > 0 || day.Unpriced > 0) return null;
        return ((double)day.CostMin, (double)day.CostMax);
    }

    internal void Draw(Graphics graphics, Rectangle area, Font original, DisplayTheme theme, int lineHeight)
    {
        graphics.Clear(theme.BackgroundColor);
        links.Clear();
        int extra = Notice.Length > 0 ? 1 : 0;
        int line = Math.Max(1, Math.Min(lineHeight, Math.Max(1, area.Height / (20 + extra))));
        using var font = new Font(original.FontFamily, Math.Max(1, original.Size * Math.Min(1f, (float)line / Math.Max(1, lineHeight))), original.Style, GraphicsUnit.Pixel);
        int x = Math.Max(8, line / 2), width = Math.Max(1, area.Width - x * 2);
        int plotHeight = Math.Max(1, Math.Min(line * 8, area.Height - line * (11 + extra)));
        int y = Math.Max(0, area.Bottom - (plotHeight + line * (11 + extra)));
        var accent = ColorSafety.Selection(theme);
        void Text(string value, Rectangle bounds, Color? color = null) => TextRenderer.DrawText(graphics, value, font, bounds,
            color ?? theme.TextColor, theme.BackgroundColor, TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine);
        void Row(string value) { Text(value, new(x, y, width, line)); y += line; }
        void Rule()
        {
            using var pen = new Pen(theme.TextColor) { DashStyle = DashStyle.Dash };
            graphics.DrawLine(pen, x, y + line / 2, x + width, y + line / 2);
            y += line;
        }
        void Link(string value, int command)
        {
            var bounds = new Rectangle(x, y, Math.Min(width, TextRenderer.MeasureText(value, font).Width), line);
            Text(value, bounds, mouse is Point p && bounds.Contains(p) ? accent : null);
            links.Add((bounds, command));
            y += line;
        }
        Rule();
        Row("近7日消耗");
        Row($"{Days[0].Day:yyyy-MM-dd} 至 {Days[6].Day:yyyy-MM-dd} · 本游戏 / 当前 API 连接 · 北京时间");
        Rule();
        Row(Tokens ? "Token 用量（输入 + 输出）" : "花费（人民币元，估算区间）");

        int left = Math.Min(line * 6, Math.Max(line * 2, width / 4));
        plot = new(x + left, y + line / 2, Math.Max(1, width - left - line), Math.Max(1, plotHeight - line * 3 / 2));
        var values = Enumerable.Range(0, 7).Select(Value).ToArray();
        double maximum = values.Where(v => v.HasValue).Select(v => v!.Value.High).DefaultIfEmpty(0).Max();
        maximum = maximum <= 0 ? 1 : maximum * 1.15;
        if (Tokens) maximum = Math.Max(4, Math.Ceiling(maximum / 4) * 4);
        PointF Coordinate(int index, double value) => new(plot.Left + index * plot.Width / 6f, plot.Bottom - (float)(value / maximum * plot.Height));
        using var grid = new Pen(Color.FromArgb(80, theme.TextColor)) { DashStyle = DashStyle.Dot };
        using var highPen = new Pen(accent, Math.Max(1, line / 12f));
        using var lowPen = new Pen(theme.TextColor, Math.Max(1, line / 16f)) { DashStyle = DashStyle.Dash };
        using var band = new SolidBrush(Color.FromArgb(35, accent));
        using var marker = new SolidBrush(accent);
        using var select = new Pen(Color.FromArgb(130, accent)) { DashStyle = DashStyle.Dash };
        var smoothing = graphics.SmoothingMode;
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        for (int i = 0; i <= 4; i++)
        {
            double tick = maximum * i / 4;
            float top = plot.Bottom - i * plot.Height / 4f;
            graphics.DrawLine(grid, plot.Left, top, plot.Right, top);
            string label = tick.ToString(Tokens ? "0" : "0.######", CultureInfo.InvariantCulture);
            Text(label, new(x, (int)top - line / 2, Math.Max(1, left - line / 2), line));
        }
        graphics.DrawLine(select, Coordinate(Selected, 0).X, plot.Top, Coordinate(Selected, 0).X, plot.Bottom);
        for (int i = 0; i < 7; i++)
        {
            float position = Coordinate(i, 0).X;
            string label = Days[i].Day.ToString("MM-dd", CultureInfo.InvariantCulture);
            int labelWidth = Math.Max(line * 3, TextRenderer.MeasureText(label, font).Width);
            int labelLeft = Math.Clamp((int)position - labelWidth / 2, x, Math.Max(x, area.Right - x - labelWidth));
            Text(label, new(labelLeft, plot.Bottom + 2, labelWidth, line), i == Selected ? accent : null);
            if (values[i] is not { } value) continue;
            var upper = Coordinate(i, value.High);
            var lower = Coordinate(i, value.Low);
            if (i > 0 && values[i - 1] is { } previous)
            {
                var previousHigh = Coordinate(i - 1, previous.High);
                var previousLow = Coordinate(i - 1, previous.Low);
                if (!Tokens) graphics.FillPolygon(band, [previousHigh, upper, lower, previousLow]);
                graphics.DrawLine(highPen, previousHigh, upper);
                if (!Tokens) graphics.DrawLine(lowPen, previousLow, lower);
            }
            float radius = Math.Max(2, line / 8f);
            graphics.FillEllipse(marker, upper.X - radius, upper.Y - radius, radius * 2, radius * 2);
            if (!Tokens) graphics.DrawEllipse(lowPen, lower.X - radius, lower.Y - radius, radius * 2, radius * 2);
        }
        graphics.SmoothingMode = smoothing;
        if (values.All(value => value == null))
            Text(Tokens ? "近7日暂无可绘制的用量记录" : "近7日暂无可完整估算的花费记录，可切换 Token 用量", new(plot.Left, plot.Top + plot.Height / 2 - line / 2, plot.Width, line));
        y += plotHeight;
        Row(Tokens ? "仅统计已返回的 Token；取消或异常请求可能未计入。" : "实线：估算上限；虚线：下限。无记录或无法估算时留空，实际以账单为准。");
        Row(Details(Selected));
        if (Notice.Length > 0) Row(Notice);
        Rule();
        Link("[1] 花费折线图", 1);
        Link("[2] Token 用量折线图", 2);
        Link("[0] " + ReturnLabel, 0);
    }
}
