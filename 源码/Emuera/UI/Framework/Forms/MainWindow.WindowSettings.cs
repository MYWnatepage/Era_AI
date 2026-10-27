using System;
using System.Drawing;
using System.IO;
using System.Windows.Forms;
using MinorShift.Emuera.NativeDisplay;
using MinorShift.Emuera.Runtime.Config;

namespace MinorShift.Emuera.Forms;

internal sealed partial class MainWindow
{
    private readonly WindowSettingsStore windowSettingsStore = new(Path.Combine(Program.ExeDir, "配置", "window-settings.json"));
    private void LoadWindowSettings()
    {
        var value = windowSettingsStore.Load();
        if (value != null) Config.ApplyWindowSettings(value.Width, value.Height, value.Maximized);
    }
    public Size WindowContentSize => new(mainPicBox.Width, mainPicBox.Height + richTextBox1.Height);
    public Size WindowContentLimit
    {
        get
        {
            var area = Screen.FromControl(this).WorkingArea;
            return new(Math.Max(1, area.Width - (Width - ClientSize.Width) - vScrollBar.Width),
                Math.Max(1, area.Height - (Height - ClientSize.Height) - (menuStrip.Visible ? menuStrip.Height : 0)));
        }
    }
    public void SyncWindowDrawingWidth() => Config.UpdateWindowDrawingWidth(mainPicBox.Width);
    public string SetGameWindowSize(string text, bool maximize)
    {
        WindowSettings value;
        if (!maximize)
        {
            if (string.IsNullOrWhiteSpace(text)) return "";
            if (!WindowSettings.TryParse(text, out value)) return "请输入宽和高，例如 1280×800；宽 640～7680，高 360～4320。";
        }
        else
        {
            value = windowSettingsStore.Load() ?? new WindowSettings(
                ConfigData.Instance.GetConfigValue<int>(ConfigCode.WindowX),
                ConfigData.Instance.GetConfigValue<int>(ConfigCode.WindowY));
            if (!value.IsValid) value = new WindowSettings(1280, 800);
        }
        var limit = WindowContentLimit;
        value = value with { Width = Math.Min(value.Width, limit.Width), Height = Math.Min(value.Height, limit.Height), Maximized = maximize };
        if (!value.IsValid) return "当前屏幕可用空间不足，请先调整系统显示设置。";
        if (!windowSettingsStore.Save(value)) return "窗口设置保存失败，请检查游戏目录的写入权限。";
        SuspendLayout();
        try
        {
            _isWidthLocked = false;
            MinimumSize = Size.Empty;
            MaximumSize = Size.Empty;
            FormBorderStyle = FormBorderStyle.Sizable;
            MaximizeBox = true;
            WindowState = FormWindowState.Normal;
            ClientSize = new Size(value.Width + vScrollBar.Width, value.Height + (menuStrip.Visible ? menuStrip.Height : 0));
            var area = Screen.FromControl(this).WorkingArea;
            Location = new Point(Math.Clamp(Left, area.Left, Math.Max(area.Left, area.Right - Width)),
                Math.Clamp(Top, area.Top, Math.Max(area.Top, area.Bottom - Height)));
            MaximizedBounds = area;
            if (maximize) WindowState = FormWindowState.Maximized;
            Config.ApplyWindowSettings(value.Width, value.Height, maximize);
        }
        finally { ResumeLayout(true); }
        ApplyTypographyLayout();
        SyncWindowDrawingWidth();
        mainPicBox.Invalidate();
        return "";
    }
}
