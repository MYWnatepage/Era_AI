using System;
using System.Drawing;
using System.Windows.Forms;
using MinorShift.Emuera.NativeDisplay;
namespace MinorShift.Emuera.Forms;
internal sealed partial class MainWindow
{
    public void ApplyTypographyLayout()
    {
        var configHeight = Runtime.Config.Config.LineHeight;
        richTextBox1.Font = new Font(Runtime.Config.Config.FontName, Runtime.Config.Config.FontSize, FontStyle.Regular, GraphicsUnit.Pixel);
        richTextBox1.Top = ClientSize.Height - configHeight;
        richTextBox1.Height = configHeight;
        mainPicBox.Height = System.Math.Max(1, ClientSize.Height - mainPicBox.Top - configHeight);
        mainPicBox.Invalidate();
    }
    private GameColorEditor gameColorEditor;
    public void ShowDisplayEditor(GameColorEditor editor)
    {
        gameColorEditor = editor;
        richTextBox1.Clear();
        richTextBox1.Focus();
        mainPicBox.MouseUp += DisplayEditorMouseUp;
        mainPicBox.Invalidate();
    }
    private void DisplayEditorMouseUp(object sender, MouseEventArgs e)
    {
        gameColorEditor?.MouseUp();
        mainPicBox.Capture = false;
    }
    private bool DrawDisplayEditor(Graphics graphics)
    {
        if (gameColorEditor == null) return false;
        gameColorEditor.Draw(graphics, mainPicBox.ClientRectangle, richTextBox1.Font, DisplayRuntime.Current, Runtime.Config.Config.LineHeight);
        return true;
    }
    private bool DisplayEditorKey(KeyEventArgs e)
    {
        if (gameColorEditor == null) return false;
        if (e.KeyCode == Keys.Enter)
        {
            var input = richTextBox1.Text;
            richTextBox1.Clear();
            gameColorEditor.Submit(input);
            e.SuppressKeyPress = true;
        }
        else if (e.KeyCode == Keys.Escape)
        {
            richTextBox1.Clear();
            gameColorEditor.Escape();
            e.SuppressKeyPress = true;
        }
        mainPicBox.Invalidate();
        return true;
    }
    public void CloseDisplayEditor()
    {
        gameColorEditor = null;
        mainPicBox.Capture = false;
        mainPicBox.MouseUp -= DisplayEditorMouseUp;
        richTextBox1.Clear();
        richTextBox1.Focus();
        console.RefreshStrings(true);
        mainPicBox.Invalidate();
    }
}
