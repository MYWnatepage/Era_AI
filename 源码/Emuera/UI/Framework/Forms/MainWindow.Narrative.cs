using System;
using System.Drawing;
using System.IO;
using System.Windows.Forms;
using MinorShift.Emuera.NativeDisplay;

namespace MinorShift.Emuera.Forms;

internal sealed partial class MainWindow
{
    private NarrativeDisplayStore narrativeStore;
    private NarrativeDisplaySettings narrativeSettings;
    private NarrativeDisplayEditor narrativeEditor;
    private bool narrativeReadOnly;
    private long ignoreNarrativeKeysUntil;
    internal NarrativeDisplaySettings NarrativeSettings => narrativeSettings ??= (narrativeStore ??= new(Path.Combine(Program.ExeDir, "配置", "narrative-display.json"))).Load();
    internal bool NarrativeEditing => narrativeEditor != null;
    private bool NarrativeActive => console?.NarrativeActive == true;
    internal string SetNarrativeSettings(NarrativeDisplaySettings value, bool save)
    {
        _ = NarrativeSettings;
        if (!value.IsValid) return "显示速度应为1～120字/秒。";
        if (save && !narrativeStore.Save(value)) return "剧情显示设置保存失败，请检查游戏目录写入权限。";
        narrativeSettings = value; return "";
    }
    internal void ShowNarrativeEditor()
    {
        if (NarrativeEditing || NarrativeActive) return;
        MusicShortcuts = false;
        narrativeEditor = new(NarrativeSettings, SetNarrativeSettings, () =>
        {
            narrativeEditor = null; mainPicBox.Capture = false;
            mainPicBox.MouseUp -= NarrativeMouseUp;
            richTextBox1.Clear(); ignoreNarrativeKeysUntil = Environment.TickCount64 + 150;
            console.RefreshStrings(true); mainPicBox.Invalidate();
        });
        richTextBox1.Clear(); richTextBox1.Focus();
        mainPicBox.MouseUp += NarrativeMouseUp; mainPicBox.Invalidate();
    }
    private void NarrativeMouseUp(object sender, MouseEventArgs e)
    {
        narrativeEditor?.MouseUp(); mainPicBox.Capture = false; mainPicBox.Invalidate();
    }
    internal void BeginNarrativeInput()
    {
        narrativeReadOnly = richTextBox1.ReadOnly;
        richTextBox1.Clear(); richTextBox1.ReadOnly = true;
    }
    internal void EndNarrativeInput()
    {
        richTextBox1.ReadOnly = narrativeReadOnly; richTextBox1.Clear();
    }
    private bool NarrativeKey(Keys key)
    {
        if (Environment.TickCount64 < ignoreNarrativeKeysUntil) return true;
        if (NarrativeActive)
        {
            if (key is Keys.Enter or Keys.Escape or Keys.Space)
            { console.FinishNarrative(); ignoreNarrativeKeysUntil = Environment.TickCount64 + 150; }
            return true;
        }
        if (narrativeEditor == null) return false;
        if (key == Keys.Up) narrativeEditor.Speed(NarrativeSettings.CharactersPerSecond + 5);
        else if (key == Keys.Down) narrativeEditor.Speed(NarrativeSettings.CharactersPerSecond - 5);
        else if (key == Keys.Left) narrativeEditor.Mode(true);
        else if (key == Keys.Right) narrativeEditor.Mode(false);
        else if (key == Keys.Escape) narrativeEditor.Close();
        else if (key == Keys.Enter) { narrativeEditor.Submit(richTextBox1.Text); richTextBox1.Clear(); }
        else return false;
        mainPicBox.Invalidate(); return true;
    }
    private bool NarrativeMouseDown(MouseEventArgs e)
    {
        if (NarrativeActive)
        {
            if (e.Button == MouseButtons.Left) { console.FinishNarrative(); ignoreNarrativeKeysUntil = Environment.TickCount64 + 150; }
            return true;
        }
        if (narrativeEditor == null) return false;
        if (e.Button == MouseButtons.Left)
        {
            mainPicBox.Capture = true; narrativeEditor.MouseDown(e.Location);
            if (narrativeEditor == null) mainPicBox.Capture = false;
            richTextBox1.Focus(); mainPicBox.Invalidate();
        }
        return true;
    }
    private bool DrawNarrativeEditor(Graphics graphics)
    {
        if (narrativeEditor == null) return false;
        console?.OnPaint(graphics);
        narrativeEditor.Draw(graphics, mainPicBox.ClientRectangle, richTextBox1.Font, DisplayRuntime.Current, Runtime.Config.Config.LineHeight);
        return true;
    }
}
