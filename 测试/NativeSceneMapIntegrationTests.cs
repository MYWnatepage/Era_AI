using System.Collections;
using System.Diagnostics;
using System.Drawing;
using System.Reflection;
using System.Numerics;
using System.Runtime.Loader;
using System.Windows.Forms;

internal static class NativeSceneMapIntegrationTests
{
    private const BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
    private static object? Get(object obj, string name) => obj.GetType().GetProperty(name, Flags)?.GetValue(obj) ?? obj.GetType().GetField(name, Flags)?.GetValue(obj);
    private static object? Call(object obj, string name, params object?[] args) => obj.GetType().GetMethod(name, Flags, null, args.Select(a => a!.GetType()).ToArray(), null)!.Invoke(obj, args);
    private static void Check(bool value, string message)
    {
        if (!value) throw new Exception(message);
        Console.WriteLine("PASS native GPU map: " + message);
    }

    private static void Set(object obj, string name, object? value) => obj.GetType().GetProperty(name, Flags)!.SetValue(obj, value);
    private static object Site(object view, int id) => ((IEnumerable)Get(Get(view, "World")!, "Sites")!).Cast<object>()
        .Single(site => (int)Get(site, "Id")! == id);

    private static void Capture(object view, string name)
    {
        string? output = Environment.GetEnvironmentVariable("ERA_SCENE_MAP_PREVIEW");
        if (string.IsNullOrWhiteSpace(output)) return;
        Directory.CreateDirectory(output);
        Call(view, "Capture", Path.Combine(output, name));
    }

    private static void VerifyLocationDetails(object view)
    {
        var detail = (Panel)Get(view, "DetailControl")!;
        foreach (int id in new[] { 1, 2, 3, 4, 5 })
        {
            Call(view, "Select", id);
            var site = Site(view, id);
            var labels = detail.Controls.Cast<Control>().Single().Controls.OfType<Label>().OrderBy(label => label.Top).ToArray();
            string text = (string)Get(view, "DetailText")!;
            Check(labels.Length == 5 && labels[0].Text.StartsWith((string)Get(site, "Name")!, StringComparison.Ordinal) &&
                labels[0].Text.Contains('（') && labels[0].Text.Contains('）') &&
                labels[1].Text == (string)Get(site, "Description")! &&
                labels[2].Text.StartsWith("这里的人物：", StringComparison.Ordinal) &&
                labels[3].Text.StartsWith("可进行的行动：", StringComparison.Ordinal) && labels[4].Text.StartsWith("特殊增益：", StringComparison.Ordinal),
                "place " + id + " renders heading, description, people, actions and benefits in order");
            Check(new[] { "设施：", "相邻地点：", "示意坐标", "本局", "程序", "剧本" }.All(word => !text.Contains(word)),
                "place " + id + " omits the former technical and immersion-breaking detail text");
            Check(labels[3].Text == "可进行的行动：" + (id == 4 ? "[狩猎]" : id == 5 ? "[交易]" : "无"),
                "place " + id + " shows only bracketed special actions, or none");
            Check(labels[4].Text == "特殊增益：" + (id == 1 ? "[休息：疲劳恢复 +10 点]、[入眠：疲劳恢复 +20 点]" : "无"),
                "place " + id + " shows bracketed actual bonuses, or none");
            if (id == 1)
            {
                Check(labels[0].Text.EndsWith("当前位置", StringComparison.Ordinal) &&
                    labels[2].Text.Contains("主角") && labels[2].Text.Contains("艾拉"),
                    "cabin keeps its current-place heading and actual residents visible");
            }
            else
            {
                Check(labels[2].Text == "这里的人物：未见人物",
                    "unoccupied place " + id + " does not invent people or move the cabin residents");
            }
        }
        var destinationButton = (Button)Get(view, "DestinationButton")!;
        Check(!destinationButton.Visible && destinationButton.Parent == null,
            "four-block place details do not retain the former extra destination button");
        Call(view, "Select", 1);
    }

    private static async Task VerifyRouteGestures(object view)
    {
        var surface = (Control)Get(view, "Surface")!;
        var route = Get(surface, "Route")!;
        var camera = Get(surface, "Camera")!;
        var world = Get(view, "World")!;
        float y = (float)Call(world, "HeightAt", 5f, 5f)!;
        var start = (PointF)Call(camera, "Project", new Vector3(-20, (float)Call(world, "HeightAt", -20f, 16f)!, 16), surface.Width, surface.Height)!;
        var target = (PointF)Call(camera, "Project", new Vector3(5, y, 5), surface.Width, surface.Height)!;
        var a = Point.Round(start); var b = Point.Round(target);
        double yaw = Convert.ToDouble(Get(camera, "Yaw"));
        void Mouse(string method, MouseButtons button, Point point) => Call(surface, method, new MouseEventArgs(button, 1, point.X, point.Y, 0));
        Mouse("OnMouseDown", MouseButtons.Right, a);
        for (int i = 1; i <= 10; i++) Mouse("OnMouseMove", MouseButtons.Right, new Point(a.X + (b.X - a.X) * i / 10, a.Y + (b.Y - a.Y) * i / 10));
        Mouse("OnMouseUp", MouseButtons.Right, b);
        await Task.Delay(80);
        Check((bool)Get(route, "HasRoute")! && !(bool)Get(route, "IsConfirmed")! && !(bool)Get(route, "IsDrawing")!,
            "right drag creates a pending ground route");
        var points = ((IEnumerable)Get(route, "Points")!).Cast<Vector3>().ToArray();
        Check(points[0].X == -25 && points[0].Z == 18 && (Vector3)Get(route, "EndPoint")! == points[^1] &&
            Math.Abs(Convert.ToDouble(Get(camera, "Yaw")) - yaw) < .0001,
            "right drag anchors at saved current place, keeps its exact endpoint and never rotates the view");
        Capture(view, "scene-native-route-preview.png");
        // A left drag is still camera control and must not confirm the pending plan.
        Mouse("OnMouseDown", MouseButtons.Left, b);
        Mouse("OnMouseMove", MouseButtons.Left, new Point(b.X + 30, b.Y));
        Mouse("OnMouseUp", MouseButtons.Left, new Point(b.X + 30, b.Y));
        Check(!(bool)Get(route, "IsConfirmed")!, "left dragging rotates without confirming a pending route");
        Call(surface, "OnMouseWheel", new MouseEventArgs(MouseButtons.None, 0, b.X, b.Y, 120));
        Check(((IEnumerable)Get(route, "Points")!).Cast<Vector3>().SequenceEqual(points),
            "rotation and zoom preserve every route point in world coordinates");
        Mouse("OnMouseDown", MouseButtons.Left, b); Mouse("OnMouseUp", MouseButtons.Left, b);
        Check((bool)Get(route, "IsConfirmed")!, "a single left click confirms the route");
        await Task.Delay(60); Capture(view, "scene-native-route-confirmed.png");
        Mouse("OnMouseDown", MouseButtons.Right, b); Mouse("OnMouseUp", MouseButtons.Right, b);
        Check(!(bool)Get(route, "HasRoute")! && Get(route, "EndPoint") == null && !(bool)Get(route, "IsConfirmed")!,
            "the next right click clears the route, arrow and free destination");
        // Repeating the right gesture after clearing must start a fresh plan from the same saved origin.
        Mouse("OnMouseDown", MouseButtons.Right, a); Mouse("OnMouseMove", MouseButtons.Right, b); Mouse("OnMouseUp", MouseButtons.Right, b);
        Check((bool)Get(route, "HasRoute")! && !(bool)Get(route, "IsConfirmed")!, "a new right drag can redraw after clearing");
        Call(surface, "ClearRoute");
    }

    private static async Task VerifyStableLayout(object view)
    {
        var map = (Rectangle)Get(view, "MapBounds")!;
        var detailBounds = (Rectangle)Get(view, "DetailBounds")!;
        var site = Site(view, 5);
        string original = (string)Get(site, "Description")!;
        Set(site, "Description", string.Concat(Enumerable.Repeat("这是一段检查滚动与地图尺寸的较长介绍。", 200)));
        try
        {
            for (int i = 0; i < 8; i++)
            {
                Call(view, "Select", i % 2 == 0 ? 1 : 5);
                await Task.Delay(50);
                Check((Rectangle)Get(view, "MapBounds")! == map && (Rectangle)Get(view, "DetailBounds")! == detailBounds,
                    "description changes preserve both fixed panel bounds");
            }
            Check(Math.Abs((double)map.Height / detailBounds.Height - 7.0 / 3.0) < .06,
                "map and detail panel keep the approved 7:3 split");
            var detail = (Panel)Get(view, "DetailControl")!;
            Check(detail.VerticalScroll.Visible && detail.DisplayRectangle.Height > detail.ClientSize.Height,
                "long text uses the detail panel's own scrollbar");
            detail.AutoScrollPosition = new Point(0, 80);
            int beforeScroll = (int)Get(view, "DetailScrollOffset")!;
            ((Control)Get(view, "Surface")!).Invalidate();
            await Task.Delay(80);
            Check(beforeScroll > 0 && (int)Get(view, "DetailScrollOffset")! == beforeScroll,
                "redrawing native geometry preserves detail scrolling");
        }
        finally
        {
            Set(site, "Description", original);
            Call(view, "Select", 1);
        }
    }

    internal static void Run(string assemblyPath, string gamePath)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { Observe(assemblyPath, gamePath); }
            catch (Exception error) { failure = error; }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        if (!thread.Join(TimeSpan.FromSeconds(90))) throw new Exception("Native GPU map integration test timed out.");
        if (failure != null) throw failure;
    }

    private static void Observe(string assemblyPath, string gamePath)
    {
        string folder = Path.GetDirectoryName(assemblyPath)!;
        AssemblyLoadContext.Default.Resolving += (_, name) => File.Exists(Path.Combine(folder, name.Name + ".dll"))
            ? AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.Combine(folder, name.Name + ".dll")) : null;
        var assembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(assemblyPath);
        Exception? failure = null;
        bool completed = false, busy = false;
        int step = 0;
        Control? closedView = null;
        var watch = Stopwatch.StartNew();
        using var timer = new System.Windows.Forms.Timer { Interval = 100 };
        timer.Tick += async (_, _) =>
        {
            if (busy) return;
            var form = Application.OpenForms.Cast<Form>().FirstOrDefault(f => f.GetType().FullName == "MinorShift.Emuera.Forms.MainWindow");
            if (form == null) return;
            busy = true;
            try
            {
                if (watch.Elapsed.TotalSeconds > 78) throw new Exception("Native map stalled at step " + step + ": " + Get(form, "sceneMapFailure"));
                var console = Get(form, "console");
                if (console == null || (bool)Get(console, "IsInProcess")!) return;
                var picture = (PictureBox)Get(form, "MainPicBox")!;
                var input = (RichTextBox)Get(form, "richTextBox1")!;
                string text = string.Join('\n', ((IEnumerable)Get(console, "displayLineList")!).Cast<object>());
                bool Main() => text.Contains("[10] 查看人物状态");
                bool Map() => (bool)Get(form, "SceneMapActive")!;
                void Enter(string value) => Call(console, "PressEnterKey", false, value, false);
                void Key(Keys key) => Call(form, "richTextBox1_KeyDown", input, new KeyEventArgs(key));
                void Click(Point point) => Call(form, "mainPicBox_MouseDown", picture, new MouseEventArgs(MouseButtons.Left, 1, point.X, point.Y, 0));
                void Paint()
                {
                    using var bitmap = new Bitmap(Math.Max(1, picture.Width), Math.Max(1, picture.Height));
                    using var graphics = Graphics.FromImage(bitmap);
                    graphics.SetClip(picture.ClientRectangle);
                    Call(form, "mainPicBox_Paint", picture, new PaintEventArgs(graphics, picture.ClientRectangle));
                }
                void Unchanged() => Check(File.ReadAllText(Path.Combine(gamePath, "map-before.txt")) == File.ReadAllText(Path.Combine(gamePath, "map-current.txt")),
                    "viewing, rotating and marking preserve the saved world snapshot");
                Control? view = Get(form, "sceneMapNative") as Control;
                bool Ready() => view != null && (bool)Get(view, "Ready")!;
                if (Map() && view == null)
                    throw new Exception("Native GPU map unexpectedly fell back: " + Get(form, "sceneMapFailure"));

                if (step == 0 && text.Contains("[0] 开始新游戏")) { Enter("0"); step++; }
                else if (step == 1 && text.Contains("剧本选择")) { Enter("1"); step++; }
                else if (step == 2 && Main())
                {
                    Check((string)Call(form, "SetGameWindowSize", "1280×720", false)! == "", "host accepts 1280×720");
                    NativeMapMetrics.Write("before-map");
                    Enter("12"); step++;
                }
                else if (step == 3 && Map() && Ready())
                {
                    var sites = ((IEnumerable)Get(Get(view!, "World")!, "Sites")!).Cast<object>().ToArray();
                    Check(sites.Length == 5 && (int)Get(view!, "SelectedId")! == 1 && (int)Get(Get(view!, "World")!, "CurrentId")! == 1,
                        "native GPU map uses actual five known scenes and saved current location");
                    Check(Get(view!, "DestinationId") == null, "opening does not invent a destination");
                    await Task.Delay(250);
                    NativeMapMetrics.Write("map-open");
                    Capture(view!, "scene-native-overview.png");
                    VerifyLocationDetails(view!);
                    await VerifyStableLayout(view!);
                    await VerifyRouteGestures(view!);

                    Call(view!, "Select", 5);
                    Check((string)Get(view!, "DetailText")! is string detail && detail.Contains((string)Get(Site(view!, 5), "Description")!),
                        "inspecting a place shows its saved description");
                    Call(view!, "MarkDestination", 5);
                    Check((int)Get(view!, "SelectedId")! == 5 && (int)Get(view!, "DestinationId")! == 5 &&
                        (string)Get(Get(form, "sceneMap")!, "CurrentLocation")! == "森林边的小屋",
                        "native destination is separate from saved character location");
                    Unchanged();
                    Capture(view!, "scene-native-destination.png");

                    var surface = (Control)Get(view!, "Surface")!;
                    var camera = Get(surface, "Camera")!;
                    double yawBefore = Convert.ToDouble(Get(camera, "Yaw")), zoomBefore = Convert.ToDouble(Get(camera, "Distance"));
                    Call(surface, "OnMouseDown", new MouseEventArgs(MouseButtons.Left, 1, surface.Width / 2, surface.Height / 2, 0));
                    Call(surface, "OnMouseMove", new MouseEventArgs(MouseButtons.Left, 0, surface.Width / 2 + 80, surface.Height / 2 + 12, 0));
                    Call(surface, "OnMouseUp", new MouseEventArgs(MouseButtons.Left, 1, surface.Width / 2 + 80, surface.Height / 2 + 12, 0));
                    Call(surface, "OnMouseWheel", new MouseEventArgs(MouseButtons.None, 0, surface.Width / 2, surface.Height / 2, 120));
                    await Task.Delay(100);
                    Check(Math.Abs(Convert.ToDouble(Get(camera, "Yaw")) - yawBefore) > .1 &&
                        Math.Abs(Convert.ToDouble(Get(camera, "Distance")) - zoomBefore) > 1,
                        "actual native mouse handlers rotate and zoom the camera");
                    NativeMapMetrics.Write("map-interacted");

                    var display = assembly.GetType("MinorShift.Emuera.NativeDisplay.DisplayRuntime")!;
                    var oldTheme = display.GetProperty("Current", Flags)!.GetValue(null);
                    var newTheme = Activator.CreateInstance(assembly.GetType("MinorShift.Emuera.NativeDisplay.DisplayTheme")!, 0xE7B27D, 0x141018, 0x7AD7FA)!;
                    display.GetMethod("Preview", Flags)!.Invoke(null, [console, newTheme]); Paint();
                    await Task.Delay(100);
                    Check(Equals(Get(view!, "Theme"), newTheme), "native map receives configured display colours");
                    Capture(view!, "scene-native-themed.png");
                    display.GetMethod("Preview", Flags)!.Invoke(null, [console, oldTheme]); Paint();

                    form.GetType().GetField("ignoreUsageChartClicksUntil", Flags)!.SetValue(form, 0L);
                    var icon = (Rectangle)Get(form, "UsageChartButtonBounds")!;
                    Click(new(icon.Left + icon.Width / 2, icon.Top + icon.Height / 2));
                    Check((bool)Get(form, "UsageChartActive")! && Map() && !view!.Visible,
                        "usage chart hides native map without losing its view state");
                    Key(Keys.Escape);
                    Check(!(bool)Get(form, "UsageChartActive")! && Map() && view!.Visible && (int)Get(view!, "SelectedId")! == 5,
                        "chart Escape restores the same native map selection");
                    closedView = view;
                    ((Button)Get(view!, "ReturnButton")!).PerformClick(); step++;
                }
                else if (step == 4 && Main())
                {
                    Check(!Map() && Get(form, "sceneMapNative") == null && closedView!.IsDisposed,
                        "return button disposes native GPU view and resumes the action menu");
                    await Task.Delay(200);
                    NativeMapMetrics.Write("map-closed");
                    Enter("12"); step++;
                }
                else if (step == 5 && Map() && Ready())
                {
                    Check((int)Get(view!, "SelectedId")! == 1 && Get(view!, "DestinationId") == null,
                        "reopening restores saved current place without stale browsing destination");
                    closedView = view;
                    ((Control)Get(view!, "Surface")!).Focus();
                    Key(Keys.Escape); step++;
                }
                else if (step == 6 && Main())
                {
                    Check(!Map() && Get(form, "sceneMapNative") == null && closedView!.IsDisposed,
                        "Escape closes native map exactly once");
                    Enter("12"); step++;
                }
                else if (step == 7 && Map() && Ready())
                {
                    Check((string)Call(form, "SetGameWindowSize", "640×480", false)! == "", "host accepts 640×480");
                    await Task.Delay(100);
                    var back = (Button)Get(view!, "ReturnButton")!;
                    Check(back.Visible && back.Bottom <= view!.ClientSize.Height && back.Top >= 0,
                        "small native window keeps return control inside viewport");
                    Capture(view!, "scene-native-narrow.png");
                    Unchanged();
                    form.Activate(); input.Focus();
                    Check(input.Focused, "native input regains focus from native map");
                    input.Text = "0"; closedView = view; Key(Keys.Enter); step++;
                }
                else if (step == 8 && Main())
                {
                    Check(!Map() && Get(form, "sceneMapNative") == null && closedView!.IsDisposed,
                        "numeric zero disposes the third native GPU view");
                    Check(File.ReadAllText(Path.Combine(gamePath, "map-returns.txt")).Trim('\uFEFF', '\r', '\n', ' ') == "3",
                        "three return routes finish exactly once and preserve time and world state");
                    Unchanged();
                    Check(!File.Exists(Path.Combine(gamePath, "数据", "账户", "api-usage.json")), "native map makes no API request");
                    NativeMapMetrics.Write("after-three-open-close");
                    var rendererType = assembly.GetType("MinorShift.Emuera.NativeDisplay.NativeMapGl")!;
                    Check((int)rendererType.GetProperty("LiveContexts", Flags)!.GetValue(null)! == 0 &&
                        (int)rendererType.GetProperty("CleanupFailures", Flags)!.GetValue(null)! == 0,
                        "closing releases every native rendering context without cleanup failures");
                    if (Environment.GetEnvironmentVariable("ERA_MAP_STRESS") == "1")
                    {
                        for (int cycle = 0; cycle < 9; cycle++)
                        {
                            Enter("12"); await Task.Delay(100);
                            var reopened = Get(form, "sceneMapNative") as Control;
                            Check(reopened != null && (bool)Get(reopened, "Ready")!, "repeated opening creates a native GPU view");
                            ((Button)Get(reopened!, "ReturnButton")!).PerformClick(); await Task.Delay(100);
                            Check(reopened!.IsDisposed && Get(form, "sceneMapNative") == null, "repeated closing disposes the native GPU view");
                            Check((int)rendererType.GetProperty("LiveContexts", Flags)!.GetValue(null)! == 0 &&
                                (int)rendererType.GetProperty("CleanupFailures", Flags)!.GetValue(null)! == 0,
                                "repeated closing leaves no native drawing context behind");
                        }
                        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); await Task.Delay(250);
                        NativeMapMetrics.Write("after-twelve-open-close-collected");
                        Unchanged();
                    }
                    completed = true; timer.Stop(); form.Close();
                }
            }
            catch (Exception error)
            {
                failure = error; timer.Stop(); form.Close();
            }
            finally { busy = false; }
        };
        EventHandler? begin = null;
        begin = (_, _) => { Application.Idle -= begin; timer.Start(); };
        Application.Idle += begin;
        assembly.EntryPoint!.Invoke(null, [new string[] { "--ExeDir", gamePath }]);
        if (failure != null) throw failure;
        Check(completed, "actual native game completed the GPU map lifecycle");
    }
}
