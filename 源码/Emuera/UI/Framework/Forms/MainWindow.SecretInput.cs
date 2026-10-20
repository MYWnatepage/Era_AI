using System;
using System.Windows.Forms;
using MinorShift.Emuera.NativeAI;

namespace MinorShift.Emuera.Forms;

internal sealed partial class MainWindow
{
    private TextBox secretInput;
    private readonly SecretInputBuffer secretBuffer = new();
    private bool secretSubmitting;
    public bool HasSecretInputSession => secretBuffer.Active;
    public string TakeSecretInput() => secretBuffer.Take();
    public void BeginSecretInput()
    {
        EndSecretInput();
        secretBuffer.Begin();
        richTextBox1.Clear();
        richTextBox1.Enabled = false;
        console.SensitiveInput = true;
        secretInput = new TextBox
        {
            Name = "ModelApiKeyInput", AccessibleName = "API 密钥输入",
            UseSystemPasswordChar = true, Font = richTextBox1.Font,
            ForeColor = richTextBox1.ForeColor, BackColor = richTextBox1.BackColor,
            Bounds = richTextBox1.Bounds, Anchor = richTextBox1.Anchor,
            BorderStyle = BorderStyle.None,
        };
        secretInput.KeyDown += (_, e) =>
        {
            if (e.KeyCode is not (Keys.Enter or Keys.Escape)) return;
            e.SuppressKeyPress = true;
            SubmitSecretInput(e.KeyCode == Keys.Escape);
        };
        Controls.Add(secretInput);
        secretInput.BringToFront();
        secretInput.Focus();
        BeginInvoke(new Action(() => secretInput?.Focus()));
    }
    private void SubmitSecretInput(bool cancel)
    {
        if (secretInput == null || secretSubmitting) return;
        secretSubmitting = true;
        try
        {
            secretBuffer.Submit(cancel ? "" : secretInput.Text);
            secretInput.Clear();
            // 密钥只经加密缓冲传递，普通剧本输入只收到空值。
            console.PressEnterKey(false, "", true);
        }
        catch (ArgumentException e)
        {
            console.PrintSingleLine(e.Message);
            console.RefreshStrings(true);
            secretInput?.Focus();
        }
        finally { secretSubmitting = false; }
    }
    public void EndSecretInput()
    {
        if (secretInput != null)
        {
            secretInput.Clear();
            Controls.Remove(secretInput);
            secretInput.Dispose();
            secretInput = null;
            richTextBox1.Enabled = true;
            richTextBox1.Focus();
        }
        if (console != null) console.SensitiveInput = false;
    }
}
