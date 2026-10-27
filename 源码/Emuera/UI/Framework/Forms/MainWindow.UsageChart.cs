using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows.Forms;
using MinorShift.Emuera.NativeAI;
using MinorShift.Emuera.NativeDisplay;

namespace MinorShift.Emuera.Forms;

internal sealed partial class MainWindow
{
    private UsageChartPage usageChart;
    private bool usageChartHovered;
    private string usageReturnInput = "";
    private int usageReturnSelection, usageReturnSelectionLength;
    private bool usageReturnReadOnly, usageReturnMusicShortcuts, usageReturnMusicSubmenu;
    private long ignoreUsageChartClicksUntil;
    public bool UsageChartActive => usageChart != null;
    private Rectangle UsageChartButtonBounds => UsageChartButton.Bounds(mainPicBox.ClientRectangle, Runtime.Config.Config.LineHeight);
    private bool IsUsageChartButton(Point point) => console != null && DisplayRuntime.Current != null && UsageChartButtonBounds.Contains(point);
    private bool IsToolbarButton(Point point) => IsUsageChartButton(point) || IsMusicSpeaker(point);
    private string UsageChartHint => usageChart != null ? "正在查看近7日消耗" : secretInput != null ? "完成密钥输入后可查看近7日消耗" : "点击查看近7日消耗";
    private void DrawUsageChartButton(Graphics graphics)
    {
        if (console == null || DisplayRuntime.Current == null) return;
        UsageChartButton.Draw(graphics, UsageChartButtonBounds, DisplayRuntime.Current, usageChartHovered || usageChart != null);
    }
    private bool ClickUsageChartButton(MouseEventArgs e)
    {
        if (!IsUsageChartButton(e.Location)) return false;
        if (e.Button != MouseButtons.Left || usageChart != null) return true;
        // Leave password input and an executing/waiting AI turn alone.
        if (secretInput != null || DiceAnimationActive || console.IsInProcess || console.IsWaitingPrimitive || AiRuntime.HistoryBusy)
        {
            musicSpeakerTip ??= new ToolTip();
            musicSpeakerTip.SetToolTip(mainPicBox, secretInput != null ? UsageChartHint : "等待当前操作完成后查看近7日消耗");
            return true;
        }
        var today = DateOnly.FromDateTime(DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromHours(8)).DateTime);
        var days = Enumerable.Range(-6, 7).Select(i => new UsagePoint(today.AddDays(i), null)).ToArray();
        string notice = "";
        try
        {
            var connection = AiRuntime.Settings.Load();
            if (connection.Mode == "api") days = AiRuntime.Usage.SevenDays(connection);
            else notice = "当前未启用网络 API，请先在模型连接设置中配置。";
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or JsonException)
        { notice = "暂时无法读取消耗记录，请检查模型连接和数据目录；原记录已保留。"; }
        ShowUsageChart(days, "返回原页面", notice);
        return true;
    }
    public void ShowUsageChart(UsagePoint[] days, string returnLabel = "返回模型连接设置", string notice = "")
    {
        if (usageChart != null) return;
        var page = new UsageChartPage(days, CloseUsageChart, returnLabel, notice);
        usageReturnInput = richTextBox1.Text;
        usageReturnSelection = richTextBox1.SelectionStart;
        usageReturnSelectionLength = richTextBox1.SelectionLength;
        usageReturnReadOnly = richTextBox1.ReadOnly;
        usageReturnMusicShortcuts = MusicShortcuts;
        usageReturnMusicSubmenu = MusicSubmenuInput;
        usageChart = page; // Suppress the original page's TextChanged input handler before clearing text.
        if (sceneMapNative != null) sceneMapNative.Visible = false;
        MusicShortcuts = false;
        MusicSubmenuInput = false;
        richTextBox1.ReadOnly = false;
        richTextBox1.Clear();
        richTextBox1.Focus();
        mainPicBox.Invalidate();
    }
    private void CloseUsageChart()
    {
        if (usageChart == null) return;
        // Restore the draft while chart input still owns TextChanged, so it cannot submit a game action.
        richTextBox1.Text = usageReturnInput;
        richTextBox1.Select(Math.Min(usageReturnSelection, richTextBox1.TextLength),
            Math.Min(usageReturnSelectionLength, Math.Max(0, richTextBox1.TextLength - usageReturnSelection)));
        richTextBox1.ReadOnly = usageReturnReadOnly;
        MusicShortcuts = usageReturnMusicShortcuts;
        MusicSubmenuInput = usageReturnMusicSubmenu;
        ignoreUsageChartClicksUntil = Environment.TickCount64 + SystemInformation.DoubleClickTime;
        ignoreMusicClipboardUntil = ignoreUsageChartClicksUntil;
        usageChart = null;
        if (sceneMapNative != null) sceneMapNative.Visible = true;
        richTextBox1.Focus();
        console?.RefreshStrings(true);
        mainPicBox.Invalidate();
    }
    private bool DrawUsageChart(Graphics graphics)
    {
        if (usageChart == null) return false;
        usageChart.Draw(graphics, mainPicBox.ClientRectangle, richTextBox1.Font, DisplayRuntime.Current, Runtime.Config.Config.LineHeight);
        return true;
    }
    private bool UsageChartKey(Keys key)
    {
        if (usageChart == null) return false;
        if (key == Keys.Escape) { usageChart.Command(0); return true; }
        if (key == Keys.Enter)
        {
            string value = richTextBox1.Text;
            richTextBox1.Clear();
            usageChart.Submit(value);
            mainPicBox.Invalidate();
            return true;
        }
        if (key is Keys.Left or Keys.Right)
        {
            usageChart.Step(key == Keys.Left ? -1 : 1);
            mainPicBox.Invalidate();
            return true;
        }
        return false;
    }
}
