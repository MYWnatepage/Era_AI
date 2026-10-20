using System;
using System.Drawing;
using System.Threading;
using System.Windows.Forms;

namespace MinorShift.Emuera.NativeAI;

internal sealed class AiSettingsForm : Form
{
    private readonly ComboBox mode = new() { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
    private readonly TextBox address = new() { Dock = DockStyle.Fill };
    private readonly TextBox model = new() { Dock = DockStyle.Fill };
    private readonly TextBox key = new() { Dock = DockStyle.Fill, UseSystemPasswordChar = true };
    private readonly NumericUpDown timeout = new() { Minimum = 10, Maximum = 600, Value = 180, Dock = DockStyle.Left, Width = 120 };
    private readonly Label status = new() { AutoSize = true, MaximumSize = new Size(580, 0), ForeColor = Color.DimGray };
    private readonly Button test = new() { Text = "测试连接", AutoSize = true };
    private readonly Button save = new() { Text = "保存设置", AutoSize = true };
    private readonly CancellationTokenSource closing = new();
    private readonly AiSettingsStore store;
    private readonly AiClient client;
    private AiSettings initial = new();
    private string initialKey = "";

    public AiSettingsForm(AiSettingsStore store, AiClient client)
    {
        this.store = store;
        this.client = client;
        Text = "AI 设置";
        Font = new Font("Microsoft YaHei UI", 10);
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(720, 580);
        MinimumSize = new Size(700, 600);
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        StartPosition = FormStartPosition.CenterParent;
        MaximizeBox = false;
        var layout = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(24), ColumnCount = 2, RowCount = 8 };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 115));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        Controls.Add(layout);
        void Row(int index, string name, Control control)
        {
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.Controls.Add(new Label { Text = name, AutoSize = true, Margin = new Padding(0, 8, 0, 15) }, 0, index);
            control.Margin = new Padding(0, 3, 0, 12);
            layout.Controls.Add(control, 1, index);
        }
        mode.Items.AddRange(["本地大模型", "API 服务"]);
        Row(0, "接入方式", mode);
        Row(1, "服务根地址", address);
        Row(2, "模型名称", model);
        Row(3, "API 密钥", key);
        Row(4, "超时（秒）", timeout);
        for (var row = 5; row < 8; row++) layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var hint = new Label
        {
            Text = "本地示例：http://127.0.0.1:11434/v1\nAPI 地址由服务商提供，通常以 /v1 结尾。\n密钥按当前 Windows 用户加密保存；本地服务未启用认证时可留空。",
            AutoSize = true, MaximumSize = new Size(590, 0), ForeColor = Color.DimGray,
        };
        layout.Controls.Add(hint, 0, 5); layout.SetColumnSpan(hint, 2);
        var buttons = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight, Margin = new Padding(0, 18, 0, 12) };
        var close = new Button { Text = "关闭", AutoSize = true, DialogResult = DialogResult.Cancel };
        buttons.Controls.AddRange([save, test, close]);
        layout.Controls.Add(buttons, 0, 6); layout.SetColumnSpan(buttons, 2);
        layout.Controls.Add(status, 0, 7); layout.SetColumnSpan(status, 2);
        CancelButton = close;
        try { initial = store.Load(); initialKey = initial.GetKey(); }
        catch (Exception e) { status.Text = e.Message; }
        mode.SelectedIndex = initial.Mode == "api" ? 1 : 0;
        address.Text = initial.BaseUrl;
        model.Text = initial.Model;
        key.Text = initialKey;
        timeout.Value = Math.Clamp(initial.TimeoutSeconds, 10, 600);
        save.Click += (_, _) =>
        {
            try { store.Save(Read()); status.Text = "设置已保存，下次对话立即使用。"; }
            catch (Exception e) { status.Text = e.Message; }
        };
        test.Click += async (_, _) =>
        {
            try
            {
                var settings = Read();
                settings.Validate();
                test.Enabled = save.Enabled = false;
                status.Text = "正在请求模型，测试可能消耗少量 API 额度……";
                var result = await client.SendAsync(settings, new AiRequest("请用一句简体中文回复：连接成功。", "连接测试", "connection_test", "", 64, 0, "简洁"), closing.Token);
                if (!IsDisposed) status.Text = result.Success ? "连接成功：" + result.Text : result.Text;
            }
            catch (Exception e) { if (!IsDisposed) status.Text = e is ArgumentException or InvalidOperationException ? e.Message : "连接测试失败，请检查设置。"; }
            finally { if (!IsDisposed) test.Enabled = save.Enabled = true; }
        };
        FormClosing += (_, _) => closing.Cancel();
    }

    private AiSettings Read() => new()
    {
        Mode = mode.SelectedIndex == 1 ? "api" : "local",
        BaseUrl = address.Text.Trim().TrimEnd('/'),
        Model = model.Text.Trim(),
        ProtectedKey = key.Text == initialKey ? initial.ProtectedKey : SecretProtection.Protect(key.Text.Trim()),
        TimeoutSeconds = (int)timeout.Value,
    };
    protected override void Dispose(bool disposing)
    {
        if (disposing) { closing.Cancel(); closing.Dispose(); }
        base.Dispose(disposing);
    }
}
