using System.Collections;
using System.Diagnostics;
using System.Drawing;
using System.Numerics;
using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using System.Windows.Forms;

internal static class RouteMovementIntegrationTests
{
    private const BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
    private static object? Get(object obj, string name) => obj.GetType().GetProperty(name, Flags)?.GetValue(obj) ?? obj.GetType().GetField(name, Flags)?.GetValue(obj);
    private static object? Call(object obj, string name, params object[] args) => obj.GetType().GetMethod(name, Flags, null, args.Select(a => a.GetType()).ToArray(), null)!.Invoke(obj, args);
    private static void Check(bool value, string text)
    { if (!value) throw new Exception(text); Console.WriteLine("PASS route movement: " + text); }
    internal static void Run(string assemblyPath, string gamePath)
    {
        Exception? failure = null;
        var thread = new Thread(() => { try { Observe(assemblyPath, gamePath); } catch (Exception error) { failure = error; } });
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        if (!thread.Join(TimeSpan.FromSeconds(65))) throw new Exception("Route movement timed out");
        if (failure != null) throw failure;
    }
    private static void Observe(string assemblyPath, string gamePath)
    {
        string folder = Path.GetDirectoryName(assemblyPath)!;
        AssemblyLoadContext.Default.Resolving += (_, name) => File.Exists(Path.Combine(folder, name.Name + ".dll"))
            ? AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.Combine(folder, name.Name + ".dll")) : null;
        var assembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(assemblyPath);
        Exception? failure = null; bool finished = false; int step = 0;
        string firstSnapshot = "", cancelledSnapshot = "", savedSnapshot = "", disabledSnapshot = ""; double distance1 = 0, distance2 = 0;
        bool checkedDisabledInput = false;
        bool dismissedDisabledNotice = false;
        var checkedDisabledPages = new HashSet<int>();
        var watch = Stopwatch.StartNew(); using var timer = new System.Windows.Forms.Timer { Interval = 100 };
        timer.Tick += (_, _) =>
        {
            var form = Application.OpenForms.Cast<Form>().FirstOrDefault(f => f.GetType().FullName == "MinorShift.Emuera.Forms.MainWindow");
            if (form == null) return;
            try
            {
                if (watch.Elapsed.TotalSeconds > 55) throw new Exception("Route movement stalled at " + step);
                var console = Get(form, "console"); if (console == null || (bool)Get(console, "IsInProcess")!) return;
                string text = string.Join('\n', ((IEnumerable)Get(console, "displayLineList")!).Cast<object>());
                void Enter(string value) => Call(console, "PressEnterKey", false, value, false);
                bool Main() => text.Contains("[12] 查看场景");
                if (Main() && checkedDisabledPages.Add(step))
                    foreach (var line in ((IEnumerable)Get(console, "displayLineList")!).Cast<object>().Where(line => line.ToString()!.Contains("（不可用）")))
                    {
                        Check(System.Text.RegularExpressions.Regex.IsMatch(line.ToString()!, @"^\s*\[[2-5]\]"), "disabled action retains its bracketed number");
                        Check(((IEnumerable)Get(line, "Buttons")!).Cast<object>().All(button => !(bool)Get(button, "IsButton")!),
                            "disabled action is plain text with no selectable or hoverable button");
                    }
                bool Result() => text.Contains("行动结果") && text.Contains("[0] 返回行动菜单");
                var view = Get(form, "sceneMapNative") as Control;
                bool Ready() => view != null && (bool)Get(view, "Ready")!;
                string Snapshot() => File.ReadAllText(Path.Combine(gamePath, "travel-state.txt")).TrimStart('\uFEFF');
                double Draw(float x, float z)
                {
                    var surface = Get(view!, "Surface")!; var route = Get(surface, "Route")!;
                    Check((bool)Call(route, "Begin")! && (bool)Call(route, "Append", x, z)!, "route begins on ground and keeps free endpoint");
                    Call(route, "Finish");
                    Call(view!, "RefreshRouteStatus");
                    Check(!((Button)Get(view!, "TravelButton")!).Enabled, "drawing alone does not authorize movement");
                    // Confirm through the same single-left-click handler used by a player.
                    Call(surface, "OnMouseDown", new MouseEventArgs(MouseButtons.Left, 1, 5, 5, 0));
                    Call(surface, "OnMouseUp", new MouseEventArgs(MouseButtons.Left, 1, 5, 5, 0));
                    Check((bool)Get(route, "IsConfirmed")! && ((Button)Get(view!, "TravelButton")!).Enabled,
                        "left click confirms and enables Start movement");
                    return Convert.ToDouble(Get(route, "LengthMetres"));
                }
                switch (step)
                {
                    case 0 when text.Contains("[0] 开始新游戏"): Enter("0"); break;
                    case 1 when text.Contains("剧本选择"): Enter("1"); break;
                    case 2 when Main():
                        firstSnapshot = Snapshot(); Enter("4"); break;
                    case 3 when Ready():
                        Call(form, "SetGameWindowSize", "1280×720", false);
                        Check(((Button)Get(view!, "TravelButton")!).Visible, "action 4 opens native movement map directly");
                        distance1 = Draw(-20, 16);
                        Check(Snapshot() == firstSnapshot, "drawing and confirmation keep the original save unchanged");
                        if (Environment.GetEnvironmentVariable("ERA_MOVEMENT_PREVIEW") is string preview)
                            Call(view!, "Capture", preview);
                        ((Button)Get(view!, "TravelButton")!).PerformClick(); break;
                    case 4 when Result():
                        Check(text.Contains("移动消耗") && text.Contains("行动点"), "movement result reports distance cost");
                        Enter("0"); break;
                    case 5 when Main():
                        Check(!text.Contains("绘制的路线") && !text.Contains("具体坐标") && !text.Contains("在地图画路线"),
                            "actual scene summary and action hints use world-facing narration");
                        cancelledSnapshot = Snapshot();
                        using (var doc = JsonDocument.Parse(cancelledSnapshot))
                        {
                            var root = doc.RootElement;
                            Check(root.GetProperty("player_position").GetProperty("x").GetDouble() == -20 &&
                                root.GetProperty("player_position").GetProperty("z").GetDouble() == 16 &&
                                Math.Abs(root.GetProperty("movement_remainder").GetDouble() - distance1 / 500) < .00001 &&
                                root.GetProperty("character_locations").GetProperty("ella").GetString() == "森林边的小屋",
                                "actual ERB commit saves free coordinates and fractional points without moving Ella");
                        }
                        Check(File.ReadAllText(Path.Combine(gamePath, "travel-clock.txt")).Trim('\uFEFF', '\r', '\n') == "1,0,10",
                            "a short move consumes no full time slot and does not charge daily food");
                        Check(text.Contains("行动点") && text.Contains("疲劳点"), "action menu displays action and fatigue resource meters");
                        Enter("4"); break;
                    case 6 when Ready():
                        var route = Get(Get(view!, "Surface")!, "Route")!; Call(route, "Begin");
                        var origin = ((IEnumerable)Get(route, "Points")!).Cast<Vector3>().First();
                        Check(origin.X == -20 && origin.Z == 16, "reopened map begins the next route at the saved endpoint");
                        Call(form, "richTextBox1_KeyDown", Get(form, "richTextBox1")!, new KeyEventArgs(Keys.Escape)); break;
                    case 7 when Main():
                        Check(Snapshot() == cancelledSnapshot, "Escape cancels movement without consuming points");
                        Enter("4"); break;
                    case 8 when Ready():
                        distance2 = Draw(30, 10);
                        ((Button)Get(view!, "TravelButton")!).PerformClick(); break;
                    case 9 when Result(): Enter("0"); break;
                    case 10 when Main():
                        if (!checkedDisabledInput)
                        {
                            Check(text.Contains("[2] 入眠（不可用）"), "sleep remains numbered and unavailable away from home");
                            disabledSnapshot = Snapshot(); checkedDisabledInput = true; Enter("2"); return;
                        }
                        if (!dismissedDisabledNotice)
                        {
                            // An unavailable manual command shows its reason in WAIT.
                            // Dismiss that existing notice before issuing another menu command.
                            dismissedDisabledNotice = true; Enter(""); return;
                        }
                        Check(Snapshot() == disabledSnapshot, "manually entering an unavailable number cannot change game state");
                        using (var doc = JsonDocument.Parse(Snapshot()))
                        {
                            var clock = File.ReadAllText(Path.Combine(gamePath, "travel-clock.txt")).Trim('\uFEFF', '\r', '\n').Split(',');
                            double consumed = int.Parse(clock[1]) + doc.RootElement.GetProperty("movement_remainder").GetDouble();
                            Check(int.Parse(clock[1]) >= 1 && Math.Abs(consumed - (distance1 + distance2) / 500) < .00002 && clock[2] == "10",
                                "second route accumulates fractions into full time slots exactly once");
                        }
                        Check(!File.Exists(Path.Combine(gamePath, "数据/账户/api-usage.json")), "route movement never calls an API");
                        savedSnapshot = Snapshot(); Enter("7"); break;
                    case 11 when text.Contains("[100] 自动存档"):
                        Enter("100"); break;
                    case 12 when Main():
                        Check(Snapshot() == savedSnapshot, "loading the real autosave restores exact position and fractional action points");
                        Enter("4"); break;
                    case 13 when Ready():
                        var loadedRoute = Get(Get(view!, "Surface")!, "Route")!; Call(loadedRoute, "Begin");
                        var loadedOrigin = ((IEnumerable)Get(loadedRoute, "Points")!).Cast<Vector3>().First();
                        Check(loadedOrigin.X == 30 && loadedOrigin.Z == 10, "map after loading starts at the saved free coordinate");
                        Call(form, "richTextBox1_KeyDown", Get(form, "richTextBox1")!, new KeyEventArgs(Keys.Escape)); break;
                    case 14 when Main():
                        finished = true; timer.Stop(); form.Close(); return;
                    default: return;
                }
                step++;
            }
            catch (Exception error) { failure = error; timer.Stop(); form.Close(); }
        };
        EventHandler? begin = null;
        begin = (_, _) => { Application.Idle -= begin; timer.Start(); }; Application.Idle += begin;
        assembly.EntryPoint!.Invoke(null, [new string[] { "--ExeDir", gamePath }]);
        if (failure != null) throw failure;
        Check(finished, "real player completed route movement and cancellation");
    }
}
