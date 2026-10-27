using System.Collections;
using System.Diagnostics;
using System.Drawing;
using System.Reflection;
using System.Runtime.Loader;
using System.Windows.Forms;

// Exercise the built player's real form and mouse/key handlers, using a disposable game directory.
internal static class UsageToolbarIntegrationTests
{
    private const BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
    private static object? Get(object obj, string name) => obj.GetType().GetProperty(name, Flags)?.GetValue(obj) ?? obj.GetType().GetField(name, Flags)?.GetValue(obj);
    private static void Set(object obj, string name, object value) => obj.GetType().GetField(name, Flags)!.SetValue(obj, value);
    private static object? Call(object obj, string name, params object?[] args) => obj.GetType().GetMethod(name, Flags)!.Invoke(obj, args);
    private static void Check(bool condition, string message)
    { if (!condition) throw new Exception(message); Console.WriteLine("PASS toolbar: " + message); }

    internal static void Run(string assemblyPath, string gamePath)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { RunOnUiThread(assemblyPath, gamePath); }
            catch (Exception e) { failure = e; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        if (!thread.Join(TimeSpan.FromSeconds(40))) throw new Exception("Toolbar form test timed out.");
        if (failure != null) throw failure;
    }

    private static void RunOnUiThread(string assemblyPath, string gamePath)
    {
        string folder = Path.GetDirectoryName(assemblyPath)!;
        AssemblyLoadContext.Default.Resolving += (_, name) =>
        {
            string path = Path.Combine(folder, name.Name + ".dll");
            return File.Exists(path) ? AssemblyLoadContext.Default.LoadFromAssemblyPath(path) : null;
        };
        var player = AssemblyLoadContext.Default.LoadFromAssemblyPath(assemblyPath);
        Exception? failure = null;
        bool completed = false;
        var elapsed = Stopwatch.StartNew();
        using var timer = new System.Windows.Forms.Timer { Interval = 100 };
        timer.Tick += (_, _) =>
        {
            var form = Application.OpenForms.Cast<Form>().FirstOrDefault(f => f.GetType().FullName == "MinorShift.Emuera.Forms.MainWindow");
            if (form == null) return;
            try
            {
                var console = Get(form, "console");
                var display = player.GetType("MinorShift.Emuera.NativeDisplay.DisplayRuntime")!;
                if (display.GetProperty("Current", Flags)!.GetValue(null) == null || console == null || (bool)Get(console, "IsInProcess")!)
                {
                    if (elapsed.Elapsed.TotalSeconds > 25) throw new Exception("Game did not reach the main menu.");
                    return;
                }
                timer.Stop();
                form.Hide();
                var picture = (PictureBox)Get(form, "MainPicBox")!;
                var input = (RichTextBox)Get(form, "TextBox")!;
                Point IconPoint()
                {
                    var bounds = (Rectangle)Get(form, "UsageChartButtonBounds")!;
                    return new(bounds.Left + bounds.Width / 2, bounds.Top + bounds.Height / 2);
                }
                void Click(Point point) => Call(form, "mainPicBox_MouseDown", picture, new MouseEventArgs(MouseButtons.Left, 1, point.X, point.Y, 0));
                void Key(Keys key) => Call(form, "richTextBox1_KeyDown", input, new KeyEventArgs(key));
                void ResetClickGuard() => Set(form, "ignoreUsageChartClicksUntil", 0L);
                var drafts = "尚未发送的行动";
                input.Text = drafts; input.Select(2, 3);
                Set(form, "MusicShortcuts", true);
                Click(IconPoint());
                Check((bool)Get(form, "UsageChartActive")!, "icon mouse handler opens seven-day chart without a scenario");
                var page = Get(form, "usageChart")!;
                Check((string)Get(page, "ReturnLabel")! == "返回原页面" && ((string)Get(page, "Notice")!).Contains("网络 API"), "unconfigured API shows an explanation and returns to the caller");
                Check(!(bool)Get(form, "MusicShortcuts")! && input.Text == "", "chart takes input ownership without submitting the draft");
                Key(Keys.Escape);
                Check(!(bool)Get(form, "UsageChartActive")! && input.Text == drafts && input.SelectionStart == 2 && input.SelectionLength == 3 && (bool)Get(form, "MusicShortcuts")!, "Escape restores draft, caret selection and settings shortcuts");
                // Write only synthetic API metadata and usage into this test game's configuration.
                var ai = player.GetType("MinorShift.Emuera.NativeAI.AiRuntime")!;
                var settings = Activator.CreateInstance(player.GetType("MinorShift.Emuera.NativeAI.AiSettings")!)!;
                settings.GetType().GetProperty("Mode")!.SetValue(settings, "api");
                settings.GetType().GetProperty("BaseUrl")!.SetValue(settings, "https://example.invalid/v1");
                settings.GetType().GetProperty("Model")!.SetValue(settings, "test-model");
                Call(ai.GetField("Settings", Flags)!.GetValue(null)!, "SaveConnection", settings);
                var usage = ai.GetField("Usage", Flags)!.GetValue(null)!;
                var tokens = Activator.CreateInstance(player.GetType("MinorShift.Emuera.NativeAI.TokenUsage")!, 1000L, 200L, (long?)0)!;
                Call(usage, "Record", settings, tokens, "test-model");
                ResetClickGuard(); Click(IconPoint());
                page = Get(form, "usageChart")!;
                Check((string)Get(page, "Notice")! == "", "shortcut reads locally recorded current-account statistics");
                input.Text = "2"; Key(Keys.Enter);
                Check((bool)Get(page, "Tokens")!, "numeric command switches to Token graph");
                var music = Get(form, "Music")!;
                int volume = (int)Get(Get(music, "Settings")!, "Volume")!;
                Key(Keys.Up);
                Check((int)Get(Get(music, "Settings")!, "Volume")! == volume, "chart keys do not trigger settings volume shortcuts");
                using (var bitmap = new Bitmap(picture.Width, picture.Height))
                using (var graphics = Graphics.FromImage(bitmap))
                {
                    Call(form, "mainPicBox_Paint", picture, new PaintEventArgs(graphics, picture.ClientRectangle));
                    if (Environment.GetEnvironmentVariable("ERA_AI_USAGE_PREVIEW") is string preview) bitmap.Save(preview);
                }
                // Return through the real clickable link, then ensure its second mouse-down cannot reach gameplay.
                foreach (var link in (IEnumerable)Get(page, "links")!)
                {
                    if ((int)link.GetType().GetField("Item2")!.GetValue(link)! != 0) continue;
                    var back = (Rectangle)link.GetType().GetField("Item1")!.GetValue(link)!;
                    Click(new(back.Left + 2, back.Top + 2));
                    break;
                }
                Check(!(bool)Get(form, "UsageChartActive")! && input.Text == drafts, "clickable return restores the original page and draft");
                Click(IconPoint());
                Check(!(bool)Get(form, "UsageChartActive")!, "double-click tail cannot reopen or submit to the underlying page");
                Call(form, "ShowMusicVolumeEditor");
                var editor = Get(form, "musicVolumeEditor");
                ResetClickGuard(); Click(IconPoint()); Key(Keys.Escape);
                Check(!(bool)Get(form, "UsageChartActive")! && ReferenceEquals(editor, Get(form, "musicVolumeEditor")), "chart Escape leaves the underlying volume editor open");
                Key(Keys.Escape);
                Call(form, "BeginSecretInput");
                ResetClickGuard(); Click(IconPoint());
                Check(!(bool)Get(form, "UsageChartActive")! && Get(form, "secretInput") != null, "shortcut does not interrupt protected API key input");
                Call(form, "EndSecretInput");
                input.Text = "保留输入";
                Call(form, "BeginD20Animation", 16, "D20：16 / 固定测试结果");
                Check((bool)Get(form, "DiceAnimationActive")! && input.ReadOnly, "D20 overlay owns input while the reply is pending");
                ResetClickGuard(); Click(IconPoint()); Key(Keys.Enter); Key(Keys.Up);
                Check(!(bool)Get(form,"UsageChartActive")! && (bool)Get(form,"DiceAnimationActive")!, "chart and gameplay input cannot interrupt the die");
                Check(!(bool)Call(form,"FinishD20Animation",true)!,"completed response waits for settling and result display");
                Call(form,"BeginD20Animation",7,"D20：7 / 同一骰点");
                Key(Keys.Escape);
                var dice=Get(form,"diceAnimation")!;
                Check((bool)Get(dice,"CancelRequested")! && (int)Get(dice,"Result")! == 7,"Escape cancels the request without changing the roll");
                Call(dice,"Complete",-10.0,false);
                Check((bool)Call(form,"FinishD20Animation",false)! && !(bool)Get(form,"DiceAnimationActive")! && !input.ReadOnly && input.Text=="保留输入", "fallback finishes animation and restores original input state");
                Check(Get(form,"diceTimer")==null,"animation timer is disposed after completion");
                Check(File.Exists(Path.Combine(gamePath, "数据", "账户", "api-usage.json")), "test statistics persist only inside the disposable game");
                completed = true;
            }
            catch (Exception error) { failure = error; timer.Stop(); }
            finally { if (completed || failure != null) form.Close(); }
        };
        EventHandler? startTimer = null;
        startTimer = (_, _) => { Application.Idle -= startTimer; timer.Start(); };
        Application.Idle += startTimer;
        player.EntryPoint!.Invoke(null, [new string[] { "--ExeDir", gamePath }]);
        if (failure != null) throw failure;
        Check(completed, "real player toolbar interaction completed");
    }
}
