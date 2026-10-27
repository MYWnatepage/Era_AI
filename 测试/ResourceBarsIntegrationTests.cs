using System.Collections;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Reflection;
using System.Runtime.Loader;
using System.Text;
using System.Windows.Forms;
using MinorShift.Emuera.NativeAI;
using MinorShift.Emuera.NativeDisplay;

internal static class ResourceBarsIntegrationTests
{
    private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    private static object? Get(object obj, string name) => obj.GetType().GetProperty(name, Flags)?.GetValue(obj) ?? obj.GetType().GetField(name, Flags)?.GetValue(obj);
    private static object? Call(object obj, string name, params object?[] args) => obj.GetType().GetMethod(name, Flags)!.Invoke(obj, args);
    private static void Check(bool value, string message)
    { if (!value) throw new Exception(message); Console.WriteLine("PASS resource bars: " + message); }

    internal static void Run(string assemblyPath, string gamePath)
    {
        PrepareFixture(gamePath);
        Exception? failure = null;
        var thread = new Thread(() => { try { Observe(assemblyPath, gamePath); } catch (Exception error) { failure = error; } });
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        if (!thread.Join(TimeSpan.FromSeconds(55))) throw new Exception("Resource HUD UI timed out.");
        if (failure != null) throw failure;
    }

    private static void PrepareFixture(string gamePath)
    {
        if (!File.Exists(Path.Combine(gamePath, "resource-bars-ui-isolated.txt")))
            throw new ArgumentException("Resource UI hooks may only modify the marked isolated game copy.");
        var scenario = ScenarioFixture.Ella;
        var state = new InventoryState();
        state.BindOwners(scenario.Owners); state.BindCharacters(scenario.Characters);
        state.BindWorld(scenario); state.SetScene(scenario.InitialScene);
        state.AddInitial(1, "铜钥匙", "用于小屋门锁的铜钥匙。", "普通物品", "", 2, "player", "");
        state.AddInitial(2, "食材", "可供烹饪的食材。", "消耗品", "", 10, "player", "");
        state.EnsureCharacterProfile("player", 11, 145, 36, "健康", "无已知伤病。", true);
        state.EnsureCharacterProfile("ella", 7, 120, 22, "健康", "无已知伤病。", true);
        state.FatiguePoints = 50; state.MovementRemainder = .5m; state.Revision = 7;
        state.BindFatigue(scenario);
        File.WriteAllText(Path.Combine(gamePath, "resource-bars-seed.txt"), state.Snapshot(), new UTF8Encoding(false));

        string flow = Path.Combine(gamePath, "ERB/script/艾拉的奇幻冒险/流程.ERB");
        string text = File.ReadAllText(flow).ReplaceLineEndings("\n");
        const string start = "@SCENARIO_TEST_RAIN_ROOM_START\n";
        const string input = "$SCENARIO_TEST_RAIN_ROOM_INPUT\nESCAPE 0\nINPUT\n";
        if (!text.Contains(start, StringComparison.Ordinal) || !text.Contains(input, StringComparison.Ordinal))
            throw new Exception("Missing resource HUD fixture hook.");
        text = text.Replace(start, start + """
IF GLOBAL:899 == 0
    SAVESTR:61 '= LOADTEXT("resource-bars-seed.txt", 0, 1)
    DAY = 3
    TIME = 2
    FLAG:20 = 10
    FLAG:21 = 1
    FLAG:22 = 2
    GLOBAL:899 = 1
ENDIF

""", StringComparison.Ordinal);
        text = text.Replace(input, """
$SCENARIO_TEST_RAIN_ROOM_INPUT
SAVETEXT SAVESTR:61, "resource-bars-state.txt", 0, 1
SAVETEXT STRFORM("{DAY},{TIME},{FLAG:20}"), "resource-bars-clock.txt", 0, 1
ESCAPE 0
INPUT

""", StringComparison.Ordinal);
        File.WriteAllText(flow, text, new UTF8Encoding(true));
    }

    private sealed record Bar(object Part, object Meter, bool IsButton);
    private static List<Bar> Bars(object console)
    {
        var bars = new List<Bar>();
        foreach (object line in (IEnumerable)Get(console, "displayLineList")!)
        foreach (object button in (IEnumerable)Get(line, "Buttons")!)
        foreach (object part in (IEnumerable)Get(button, "StrArray")!)
            if (part.GetType().Name == "ResourceSlotsPart") bars.Add(new(part, Get(part, "Meter")!, (bool)Get(button, "IsButton")!));
        return bars;
    }

    private static void Observe(string assemblyPath, string gamePath)
    {
        string folder = Path.GetDirectoryName(assemblyPath)!;
        AssemblyLoadContext.Default.Resolving += (_, name) => File.Exists(Path.Combine(folder, name.Name + ".dll"))
            ? AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.Combine(folder, name.Name + ".dll")) : null;
        var assembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(assemblyPath);
        var display = assembly.GetType("MinorShift.Emuera.NativeDisplay.DisplayRuntime")!;
        var typography = assembly.GetType("MinorShift.Emuera.NativeDisplay.TypographyRuntime")!;
        var config = assembly.GetType("MinorShift.Emuera.Runtime.Config.Config")!;
        var themeType = assembly.GetType("MinorShift.Emuera.NativeDisplay.DisplayTheme")!;
        object Theme(int foreground, int background) => Activator.CreateInstance(themeType, foreground, background, -1)!;
        object dark = Theme(0xDDDDDD, 0x101012), light = Theme(0x202020, 0xFFFFFF);
        Exception? failure = null; bool finished = false; int step = 0;
        string baseline = "", clock = "";
        var watch = Stopwatch.StartNew();
        using var timer = new System.Windows.Forms.Timer { Interval = 100 };
        timer.Tick += (_, _) =>
        {
            var form = Application.OpenForms.Cast<Form>().FirstOrDefault(f => f.GetType().FullName == "MinorShift.Emuera.Forms.MainWindow");
            if (form == null) return;
            try
            {
                if (watch.Elapsed.TotalSeconds > 45) throw new Exception("Resource HUD stalled at step " + step);
                var console = Get(form, "console"); if (console == null || (bool)Get(console, "IsInProcess")!) return;
                string text = string.Join('\n', ((IEnumerable)Get(console, "displayLineList")!).Cast<object>());
                void Enter(string value) => Call(console, "PressEnterKey", false, value, false);
                bool Main() => text.Contains("[10] 查看人物状态") && Bars(console).Count == 2;
                bool People() => text.Contains("当前人物状态") && text.Contains("共 2 位人物");
                string Snapshot() => File.ReadAllText(Path.Combine(gamePath, "resource-bars-state.txt")).TrimStart('\uFEFF');
                string Clock() => File.ReadAllText(Path.Combine(gamePath, "resource-bars-clock.txt")).TrimStart('\uFEFF');
                void Size(string value) => Check((string)Call(form, "SetGameWindowSize", value, false)! == "", "window accepts " + value);
                void Font(int size) => Check((string)typography.GetMethod("Update", Flags)!.Invoke(null, [console, 2, "", (long)size])! == "", "font accepts " + size);
                void Palette(object theme) => display.GetMethod("Preview", Flags)!.Invoke(null, [console, theme]);
                void AssertBars(object theme)
                {
                    var bars = Bars(console); Check(bars.Count == 2, "HUD contains exactly two native slot bars");
                    var action = bars.Single(b => Get(b.Meter, "Kind")!.ToString() == "ActionPoints");
                    var fatigue = bars.Single(b => Get(b.Meter, "Kind")!.ToString() == "Fatigue");
                    Check((int)Get(action.Meter, "Slots")! == 8 && (decimal)Get(action.Meter, "Value")! == 5.5m &&
                        (decimal)Get(action.Meter, "Maximum")! == 8 && Get(action.Meter, "Band")!.ToString() == "Healthy", "action bar shows 5.5 remaining points in eight green slots");
                    Check((int)Get(fatigue.Meter, "Slots")! == 10 && (decimal)Get(fatigue.Meter, "Value")! == 50 &&
                        (decimal)Get(fatigue.Meter, "Maximum")! == 100 && Get(fatigue.Meter, "Band")!.ToString() == "Strained", "fatigue bar shows 50 points in ten orange slots");
                    Check(Math.Abs(Convert.ToSingle(Call(action.Meter, "FillAt", 5)) - .5f) < .00001f,
                        "actual action slot keeps its fractional half fill");
                    Check(bars.All(b => !b.IsButton), "resource shapes have no button, hover or selection focus");
                    int drawable = (int)config.GetProperty("DrawableWidth", Flags)!.GetValue(null)!;
                    foreach (var bar in bars)
                    {
                        float width = Convert.ToSingle(Get(bar.Part, "WidthF"));
                        int x = (int)Get(bar.Part, "PointX")!;
                        Check(x >= 0 && width > 0 && x + width <= drawable + 1,
                            "resource shape fits the actual text canvas without horizontal overflow");
                    }
                    Color a = (Color)Call(action.Meter, "ColorFor", theme)!, f = (Color)Call(fatigue.Meter, "ColorFor", theme)!;
                    Color bg = (Color)Get(theme, "BackgroundColor")!;
                    Check(a.ToArgb() != f.ToArgb() && ColorSafety.Contrast(a, bg) >= 4.5 && ColorSafety.Contrast(f, bg) >= 4.5,
                        "actual bar colours remain distinct and readable in the current theme");
                    string compact = text.Replace(" ", "", StringComparison.Ordinal);
                    Check(compact.Contains("5.5/8") && compact.Contains("50/100") && compact.Contains("中度") &&
                        (compact.Contains("-4") || compact.Contains("−4")), "HUD retains numeric points, fatigue state and the D20 penalty");
                }
                void SameState()
                {
                    var saved = InventoryState.Read(Snapshot());
                    Check(Snapshot() == baseline && saved.FatiguePoints == 50 && saved.MovementRemainder == .5m && Clock() == clock,
                        "resizing, recolouring, font changes and browsing do not change fatigue, position, time or food");
                }
                void Capture(string suffix)
                {
                    if (Environment.GetEnvironmentVariable("ERA_RESOURCE_HUD_PREVIEW") is not { Length: > 0 } original) return;
                    string path = Path.GetFullPath(original);
                    if (suffix.Length > 0) path = Path.Combine(Path.GetDirectoryName(path)!, Path.GetFileNameWithoutExtension(path) + suffix + ".png");
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    var scroll = (VScrollBar)Get(form, "ScrollBar")!; int old = scroll.Value;
                    var canvas = (PictureBox)Get(form, "MainPicBox")!;
                    var lines = ((IEnumerable)Get(console, "displayLineList")!).Cast<object>().ToList();
                    int lastBar = lines.FindLastIndex(line => ((IEnumerable)Get(line, "Buttons")!).Cast<object>()
                        .Any(button => ((IEnumerable)Get(button, "StrArray")!).Cast<object>()
                            .Any(part => part.GetType().Name == "ResourceSlotsPart")));
                    int lineHeight = (int)config.GetProperty("LineHeight", Flags)!.GetValue(null)!;
                    int visibleLines = Math.Max(1, canvas.Height / lineHeight);
                    try
                    {
                        // EraPictureBox uses Opaque/UserPaint/OptimizedDoubleBuffer. WM_PRINT,
                        // used by DrawToBitmap, can skip its Paint event or return its error image.
                        // Invoke the exact production Paint handler with the real canvas bounds;
                        // this includes EmueraConsole.OnPaint and the normal toolbar renderer.
                        scroll.Value = Math.Clamp(Math.Max(lastBar + 3, visibleLines), scroll.Minimum, scroll.Maximum);
                        Check((bool)Get(console, "Enabled")!, "real game renderer is initialized before capture");
                        using var bitmap = new Bitmap(canvas.Width, canvas.Height);
                        using (var graph = Graphics.FromImage(bitmap))
                        {
                            graph.SetClip(canvas.ClientRectangle);
                            using var paint = new PaintEventArgs(graph, canvas.ClientRectangle);
                            Call(form, "mainPicBox_Paint", canvas, paint);
                        }
                        object theme = display.GetProperty("Current", Flags)!.GetValue(null)!;
                        foreach (var bar in Bars(console))
                        {
                            Color expected = (Color)Call(bar.Meter, "ColorFor", theme)!;
                            int pixels = 0;
                            for (int y = 0; y < bitmap.Height; y += 2)
                            for (int x = 0; x < bitmap.Width; x += 2)
                                if (bitmap.GetPixel(x, y).ToArgb() == expected.ToArgb()) pixels++;
                            if (pixels <= 8)
                                Console.WriteLine($"Capture diagnostic: canvas={canvas.Size}, scroll={scroll.Value}/{scroll.Maximum}, lastBar={lastBar}, lines={lines.Count}, mode={config.GetProperty("TextDrawingMode", Flags)!.GetValue(null)}, expected={expected}, pixels={pixels}");
                            Check(pixels > 8, "production Paint capture contains the actual " + Get(bar.Meter, "Kind") + " coloured slots");
                        }
                        bitmap.Save(path, ImageFormat.Png);
                    }
                    finally { scroll.Value = Math.Clamp(old, scroll.Minimum, scroll.Maximum); }
                    Console.WriteLine("Saved real HUD preview from MainWindow.mainPicBox_Paint -> EmueraConsole.OnPaint: " + path);
                }
                switch (step)
                {
                    case 0 when text.Contains("[0] 开始新游戏"): Enter("0"); break;
                    case 1 when text.Contains("剧本选择"): Enter("1"); break;
                    case 2 when Main():
                        baseline = Snapshot(); clock = Clock(); AssertBars(display.GetProperty("Current", Flags)!.GetValue(null)!);
                        Size("1280×900"); Font(24); Palette(dark); Enter("10"); break;
                    case 3 when People(): Enter("0"); break;
                    case 4 when Main():
                        AssertBars(dark); SameState(); Capture(""); Palette(light); break;
                    case 5 when Main():
                        AssertBars(light); SameState(); Capture("-light"); Size("640×480"); Font(36); Enter("10"); break;
                    case 6 when People(): Enter("0"); break;
                    case 7 when Main():
                        AssertBars(light); SameState(); Capture("-compact");
                        finished = true; timer.Stop(); form.Close(); return;
                    default: return;
                }
                step++; form.Hide();
            }
            catch (Exception error) { failure = error; timer.Stop(); form.Close(); }
        };
        EventHandler? begin = null;
        begin = (_, _) => { Application.Idle -= begin; timer.Start(); }; Application.Idle += begin;
        assembly.EntryPoint!.Invoke(null, [new string[] { "--ExeDir", gamePath }]);
        if (failure != null) throw failure;
        Check(finished, "actual native game completed the two-theme, two-font and compact HUD checks");
    }
}
