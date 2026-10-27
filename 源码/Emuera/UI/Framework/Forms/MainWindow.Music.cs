using System;
using System.Drawing;
using System.Windows.Forms;
using MinorShift.Emuera.GameProc.Function;
using MinorShift.Emuera.NativeDisplay;

namespace MinorShift.Emuera.Forms;

internal sealed partial class MainWindow
{
    private MusicController music;
    public MusicController Music => music ??= new(Program.ExeDir, FunctionIdentifier.bgm);
    public bool MusicShortcuts;
    public bool MusicSubmenuInput;
    private MusicVolumeEditor musicVolumeEditor;
    private long ignoreMusicClipboardUntil;
    public bool MusicVolumeEditing => musicVolumeEditor != null;
    public void ShowMusicVolumeEditor()
    {
        if (musicVolumeEditor != null) return;
        MusicShortcuts = false;
        musicVolumeEditor = new(Music.Settings.Volume, Music.SetVolume, () =>
        {
            musicVolumeEditor = null;
            ignoreMusicClipboardUntil = Environment.TickCount64 + SystemInformation.DoubleClickTime;
            mainPicBox.Capture = false;
            mainPicBox.MouseUp -= MusicVolumeMouseUp;
            richTextBox1.Clear();
            console.RefreshStrings(true);
            mainPicBox.Invalidate();
        });
        richTextBox1.Clear();
        richTextBox1.Focus();
        mainPicBox.MouseUp += MusicVolumeMouseUp;
        mainPicBox.Invalidate();
    }
    private void MusicVolumeMouseUp(object sender, MouseEventArgs e)
    {
        musicVolumeEditor?.MouseUp();
        mainPicBox.Capture = false;
        mainPicBox.Invalidate();
    }
    private bool musicSpeakerHovered;
    private ToolTip musicSpeakerTip;
    private Rectangle MusicSpeakerBounds => MusicSpeakerButton.Bounds(mainPicBox.ClientRectangle, Runtime.Config.Config.LineHeight);
    private bool IsMusicSpeaker(Point point) => music != null && DisplayRuntime.Current != null && MusicSpeakerBounds.Contains(point);
    private string MusicSpeakerHint => (Music.Settings.Enabled && Music.Settings.Volume > 0 ? "点击静音" : "点击播放") +
        (Music.Status.Length > 0 ? "：" + Music.Status : "");
    private void DrawMusicSpeaker(Graphics graphics)
    {
        if (music == null || DisplayRuntime.Current == null) return;
        MusicSpeakerButton.Draw(graphics, MusicSpeakerBounds, DisplayRuntime.Current,
            !Music.Settings.Enabled || Music.Settings.Volume == 0, musicSpeakerHovered);
    }
    private bool MoveToolbarButtons(Point point)
    {
        bool speaker = IsMusicSpeaker(point), usage = IsUsageChartButton(point);
        if (speaker != musicSpeakerHovered || usage != usageChartHovered)
        {
            musicSpeakerHovered = speaker;
            usageChartHovered = usage;
            musicSpeakerTip ??= new ToolTip();
            musicSpeakerTip.SetToolTip(mainPicBox, usage ? UsageChartHint : speaker ? MusicSpeakerHint : "");
            mainPicBox.Invalidate(Rectangle.Union(MusicSpeakerBounds, UsageChartButtonBounds));
        }
        return speaker || usage;
    }
    private bool ClickMusicSpeaker(MouseEventArgs e)
    {
        if (!IsMusicSpeaker(e.Location)) return false;
        if (e.Button != MouseButtons.Left) return true;
        string error = Music.ToggleFromButton();
        if (error.Length == 0) musicVolumeEditor?.Set(Music.Settings.Volume, false);
        musicSpeakerTip ??= new ToolTip();
        musicSpeakerTip.SetToolTip(mainPicBox, error.Length > 0 ? error : MusicSpeakerHint);
        mainPicBox.Invalidate();
        // Refresh the settings summary without changing the user's partially entered text.
        if (!UsageChartActive && (MusicShortcuts || MusicSubmenuInput) && richTextBox1.Text.Length == 0 && console != null && !console.IsInProcess)
        {
            MusicShortcuts = false;
            MusicSubmenuInput = false;
            richTextBox1.Text = "9003";
            PressEnterKey(false, false);
        }
        return true;
    }
    private bool MusicKey(Keys key)
    {
        if (musicVolumeEditor != null)
        {
            if (key == Keys.Up) musicVolumeEditor.Set(musicVolumeEditor.Value + 10);
            else if (key == Keys.Down) musicVolumeEditor.Set(musicVolumeEditor.Value - 10);
            else if (key == Keys.Escape) musicVolumeEditor.Close();
            else if (key == Keys.Enter)
            {
                musicVolumeEditor.Submit(richTextBox1.Text);
                richTextBox1.Clear();
            }
            else return false;
            mainPicBox.Invalidate();
            return true;
        }
        // Scope shortcuts to the top-level settings INPUT, leaving gameplay and other input history alone.
        if (!MusicShortcuts || key is not (Keys.Up or Keys.Down) || richTextBox1.Text.Length != 0 || console == null || console.IsInProcess) return false;
        MusicShortcuts = false;
        richTextBox1.Text = key == Keys.Up ? "9001" : "9002";
        PressEnterKey(false, false);
        return true;
    }
}
