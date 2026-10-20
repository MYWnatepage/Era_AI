using System.Drawing;
using System.Windows.Forms;

namespace MinorShift.Emuera.Forms;

internal sealed partial class MainWindow
{
    private void InitializeMenuRendering()
    {
        var renderer = new ReadableMenuRenderer();
        menuStrip.Renderer = renderer;
        menuStrip.AutoSize = true;
        void Configure(ToolStripItemCollection items)
        {
            foreach (ToolStripItem item in items)
            {
                if (item is not ToolStripMenuItem menu) continue;
                menu.AutoSize = true;
                menu.DropDown.Renderer = renderer;
                Configure(menu.DropDownItems);
            }
        }
        Configure(menuStrip.Items);
    }

    // 显式配对菜单选中背景与文字，避免不同系统主题下选中文字消失。
    private sealed class ReadableMenuRenderer : ToolStripSystemRenderer
    {
        protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
        {
            bool selected = e.Item.Selected || e.Item.Pressed;
            using var brush = new SolidBrush(selected ? SystemColors.Highlight : SystemColors.Menu);
            e.Graphics.FillRectangle(brush, new Rectangle(Point.Empty, e.Item.Size));
        }

        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            e.TextColor = !e.Item.Enabled ? SystemColors.GrayText
                : e.Item.Selected || e.Item.Pressed ? SystemColors.HighlightText : SystemColors.MenuText;
            base.OnRenderItemText(e);
        }
    }
}
