using System;
using System.Drawing;
using System.Windows.Forms;
using MinorShift.Emuera.Runtime.Config;

namespace MinorShift.Emuera.Forms;

internal sealed partial class MainWindow
{
    private Point contextItemPoint;
    // Only current-page item buttons own these gestures. Normal text entry and other menus keep their keys.
    private bool AdjustContextItemMouse(MouseEventArgs e)
    {
        if (e.Button is not (MouseButtons.Left or MouseButtons.Right)) return false;
        return AdjustContextItem(e.Location, e.Button == MouseButtons.Left ? 1 : -1);
    }
    private bool AdjustContextItemKey(KeyEventArgs e)
    {
        if (e.KeyCode is not (Keys.Up or Keys.Down) || e.Modifiers != Keys.None) return false;
        if (console?.SelectedString?.StartsWith("@item:", StringComparison.Ordinal) != true ||
            !AdjustContextItem(contextItemPoint, e.KeyCode == Keys.Up ? 10 : -10)) return false;
        e.SuppressKeyPress = true; return true;
    }
    private bool AdjustContextItem(Point point, int delta)
    {
        if (console == null || console.IsInProcess || usageChart != null || musicVolumeEditor != null || gameColorEditor != null ||
            DiceAnimationActive || secretInput != null || vScrollBar.Value != vScrollBar.Maximum || !Config.UseMouse) return false;
        console.MoveMouse(point);
        string payload = console.SelectedString;
        if (payload == null || !payload.StartsWith("@item:", StringComparison.Ordinal) ||
            !int.TryParse(payload.AsSpan(6), out int id)) return false;
        contextItemPoint = point;
        string draft = richTextBox1.Text;
        int caret = richTextBox1.SelectionStart, length = richTextBox1.SelectionLength;
        console.PressEnterKey(false, "@adjust:" + id + ":" + delta, true);
        // A quantity gesture must not destroy a player's unfinished sentence or enter the text history.
        richTextBox1.Text = draft;
        richTextBox1.Select(Math.Min(caret, draft.Length), Math.Min(length, draft.Length - Math.Min(caret, draft.Length)));
        richTextBox1.Focus();
        console.MoveMouse(point); mainPicBox.Invalidate();
        return true;
    }
}
