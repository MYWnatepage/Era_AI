using System.Collections;
using System.Diagnostics;
using System.Drawing;
using System.Reflection;
using System.Runtime.Loader;
using System.Windows.Forms;

internal static class SceneMapIntegrationTests
{
    private const BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
    private static object? Get(object obj, string name) => obj.GetType().GetProperty(name, Flags)?.GetValue(obj) ?? obj.GetType().GetField(name, Flags)?.GetValue(obj);
    private static object? Call(object obj, string name, params object?[] args) => obj.GetType().GetMethod(name, Flags)!.Invoke(obj, args);
    private static void Check(bool value, string message)
    { if (!value) throw new Exception(message); Console.WriteLine("PASS scene map: " + message); }

    internal static void Run(string assemblyPath, string gamePath)
    {
        Exception? failure = null;
        var thread = new Thread(() => { try { Observe(assemblyPath, gamePath); } catch (Exception e) { failure = e; } });
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        if (!thread.Join(TimeSpan.FromSeconds(45))) throw new Exception("Scene map UI test timed out.");
        if (failure != null) throw failure;
    }
    private static void Observe(string assemblyPath, string gamePath)
    {
        string folder = Path.GetDirectoryName(assemblyPath)!;
        AssemblyLoadContext.Default.Resolving += (_, name) => File.Exists(Path.Combine(folder, name.Name + ".dll"))
            ? AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.Combine(folder, name.Name + ".dll")) : null;
        var assembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(assemblyPath);
        Exception? failure = null; bool completed = false; int step = 0;
        var watch = Stopwatch.StartNew();
        using var timer = new System.Windows.Forms.Timer { Interval = 100 };
        timer.Tick += (_, _) =>
        {
            var form = Application.OpenForms.Cast<Form>().FirstOrDefault(f => f.GetType().FullName == "MinorShift.Emuera.Forms.MainWindow");
            if (form == null) return;
            try
            {
                if (watch.Elapsed.TotalSeconds > 35) throw new Exception("Map navigation stalled at step " + step);
                var console = Get(form, "console");
                if (console == null || (bool)Get(console, "IsInProcess")!) return;
                form.Hide();
                var picture = (PictureBox)Get(form, "MainPicBox")!;
                var input = (RichTextBox)Get(form, "richTextBox1")!;
                string text = string.Join('\n', ((IEnumerable)Get(console, "displayLineList")!).Cast<object>());
                bool Main() => text.Contains("[10] 查看人物状态");
                bool Map() => (bool)Get(form, "SceneMapActive")!;
                void Enter(string value) => Call(console, "PressEnterKey", false, value, false);
                void Key(Keys key) => Call(form, "richTextBox1_KeyDown", input, new KeyEventArgs(key));
                void Click(Point point) => Call(form, "mainPicBox_MouseDown", picture, new MouseEventArgs(MouseButtons.Left, 1, point.X, point.Y, 0));
                void Move(Point point) => Call(form, "mainPicBox_MouseMove", picture, new MouseEventArgs(MouseButtons.None, 0, point.X, point.Y, 0));
                Bitmap Paint()
                {
                    var bitmap = new Bitmap(picture.Width, picture.Height);
                    using var graph = Graphics.FromImage(bitmap); graph.SetClip(picture.ClientRectangle);
                    Call(form, "mainPicBox_Paint", picture, new PaintEventArgs(graph, picture.ClientRectangle));
                    return bitmap;
                }
                Point Target(object page, int id)
                {
                    var target = ((IEnumerable)Get(page, "Targets")!).Cast<object>().Single(t => (int)t.GetType().GetField("Item1")!.GetValue(t)! == id);
                    return Point.Round((PointF)target.GetType().GetField("Item3")!.GetValue(target)!);
                }
                void Save(Bitmap bitmap, string name)
                {
                    if (Environment.GetEnvironmentVariable("ERA_SCENE_MAP_PREVIEW") is string output)
                    { Directory.CreateDirectory(output); bitmap.Save(Path.Combine(output, name)); }
                }
                if (step == 0 && text.Contains("[0] 开始新游戏")) { Enter("0"); step++; }
                else if (step == 1 && text.Contains("剧本选择")) { Enter("1"); step++; }
                else if (step == 2 && Main()) { Enter("12"); step++; }
                else if (step == 3 && Map())
                {
                    Check((string)Call(form, "SetGameWindowSize", "1280×720", false)! == "", "native map accepts 1280×720");
                    var page = Get(form, "sceneMap")!;
                    using var before = Paint(); Save(before, "scene-map-overview.png");
                    Check((int)Get(page, "SelectedId")! == 1, "opens on actual current location");
                    Move(Target(page, 5));
                    using var hover = Paint(); Save(hover, "scene-map-hover.png");
                    Check((int)Get(page, "HoveredId")! == 5 && (string)Get(page, "SelectedName")! == "村落集市", "real mouse handler changes location focus and detail");
                    int changed = 0;
                    for (int y = 0; y < before.Height; y += 3)
                        for (int x = 0; x < before.Width; x += 3)
                            if (before.GetPixel(x, y) != hover.GetPixel(x, y)) changed++;
                    Check(changed > 100, "hover visibly redraws map geometry and description");
                    Check(File.ReadAllText(Path.Combine(gamePath, "map-before.txt")) == File.ReadAllText(Path.Combine(gamePath, "map-current.txt")), "opening the map leaves saved state unchanged");
                    Move(Target(page, 4)); Paint().Dispose();
                    Check((int)Get(page, "HoveredId")! == 4, "each location has its own hit target");
                    Check((string)Call(form, "SetGameWindowSize", "1920×1080", false)! == "", "native map accepts 1920×1080");
                    Paint().Dispose(); Move(Target(page, 5));
                    using (var wide = Paint()) Save(wide, "scene-map-wide.png");
                    var display = assembly.GetType("MinorShift.Emuera.NativeDisplay.DisplayRuntime")!;
                    var oldTheme = display.GetProperty("Current", Flags)!.GetValue(null);
                    var newTheme = Activator.CreateInstance(assembly.GetType("MinorShift.Emuera.NativeDisplay.DisplayTheme")!, 0xE7B27D, 0x141018, 0x7AD7FA)!;
                    display.GetMethod("Preview", Flags)!.Invoke(null, [console, newTheme]);
                    using (var themed = Paint()) Save(themed, "scene-map-themed.png");
                    Check(ReferenceEquals(display.GetProperty("Current", Flags)!.GetValue(null), newTheme), "map renders the active configured colours");
                    display.GetMethod("Preview", Flags)!.Invoke(null, [console, oldTheme]);
                    var back = (Rectangle)Get(page, "ReturnBounds")!;
                    Click(new(back.Left + 10, back.Top + back.Height / 2)); step++;
                }
                else if (step == 4 && Main()) { Check(!Map(), "clicking zero return closes map and resumes action menu"); Enter("12"); step++; }
                else if (step == 5 && Map())
                {
                    var page = Get(form, "sceneMap")!;
                    Check((int)Get(page, "SelectedId")! == 1, "reopening still uses actual location rather than last browsed place");
                    Paint().Dispose(); Move(Target(page, 4)); Paint().Dispose();
                    form.GetType().GetField("ignoreUsageChartClicksUntil", Flags)!.SetValue(form, 0L);
                    var icon = (Rectangle)Get(form, "UsageChartButtonBounds")!;
                    Click(new(icon.Left + icon.Width / 2, icon.Top + icon.Height / 2));
                    Check((bool)Get(form, "UsageChartActive")! && Map(), "usage chart can overlay map without submitting a scene action");
                    Key(Keys.Escape);
                    Check(!(bool)Get(form, "UsageChartActive")! && Map() && (int)Get(page, "SelectedId")! == 4, "chart Escape restores the same map focus");
                    Key(Keys.Escape); step++;
                }
                else if (step == 6 && Main()) { Check(!Map(), "map Escape returns to action menu"); Enter("12"); step++; }
                else if (step == 7 && Map())
                {
                    Check((string)Call(form, "SetGameWindowSize", "640×480", false)! == "", "native map accepts 640×480");
                    using var narrow = Paint(); Save(narrow, "scene-map-narrow.png");
                    var page = Get(form, "sceneMap")!;
                    Move(Target(page, 3)); Paint().Dispose();
                    Check((int)Get(page, "HoveredId")! == 3, "narrow window still supports independent location focus");
                    input.Text = "0"; Key(Keys.Enter); step++;
                }
                else if (step == 8 && Main())
                {
                    Check(!Map() && File.ReadAllText(Path.Combine(gamePath, "map-returns.txt")).Trim('\uFEFF', '\r', '\n', ' ') == "3", "numeric zero and all three return routes preserve items, time and world state");
                    completed = true; timer.Stop(); form.Close();
                }
            }
            catch (Exception error) { failure = error; timer.Stop(); form.Close(); }
        };
        EventHandler? begin = null;
        begin = (_, _) => { Application.Idle -= begin; timer.Start(); };
        Application.Idle += begin;
        assembly.EntryPoint!.Invoke(null, [new string[] { "--ExeDir", gamePath }]);
        if (failure != null) throw failure;
        Check(completed, "actual native player completed map interaction sequence");
    }
}
