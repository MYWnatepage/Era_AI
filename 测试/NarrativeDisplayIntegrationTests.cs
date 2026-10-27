using System.Collections;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Reflection;
using System.Runtime.Loader;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Forms;

internal static class NarrativeDisplayIntegrationTests
{
    const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    static object? Get(object obj, string name) => obj.GetType().GetProperty(name, Flags)?.GetValue(obj) ?? obj.GetType().GetField(name, Flags)?.GetValue(obj);
    static object? Call(object obj, string name, params object?[] args) => obj.GetType().GetMethod(name, Flags)!.Invoke(obj, args);
    static void Check(bool ok, string message) { if (!ok) throw new Exception(message); Console.WriteLine("PASS narrative: " + message); }
    static string Compact(string text) => Regex.Replace(Regex.Replace(text, @"<[^>]*>", ""), @"\s+", "");
    internal static void Run(string assemblyPath, string gamePath, string mode)
    {
        if (!File.Exists(Path.Combine(gamePath, "narrative-isolated.txt"))) throw new Exception("Only use isolated game fixture.");
        string story = "晨光穿过窗隙，艾拉推开木门。" + string.Concat(Enumerable.Range(1, 65).Select(i => $"第{i}阵风吹过树林，溪水绕过岸边的石头🍂。")) + "\n\n她回过头说：今天的天空真晴朗。\n这段文字到这里结束。";
        if (mode == "natural") story = "风吹过树林🍂。\n\n这段文字到这里结束。";
        File.WriteAllText(Path.Combine(gamePath, "narrative-story.txt"), story, new UTF8Encoding(false));
        string flow = Path.Combine(gamePath, "ERB/script/艾拉的奇幻冒险/流程.ERB");
        string hook = """
IF GLOBAL:898 == 0
    GLOBAL:898 = 1
    SAVESTR:19 '= LOADTEXT("narrative-story.txt", 0, 1)
    SAVESTR:20 '= ""
    SAVETEXT SAVESTR:61, "narrative-before.txt", 0, 1
    SAVETEXT STRFORM("{DAY},{TIME}"), "narrative-before-clock.txt", 0, 1
""" + "\n" + (mode == "review" ? "    CALL RAIN_ROOM_REVIEW_ITEMS, SAVESTR:19, \"\", \"\", \"\"\n" : "    CALL SCENARIO_TEST_RAIN_ROOM_SHOW_RESPONSE, 0\n") + """
    SAVETEXT SAVESTR:61, "narrative-after.txt", 0, 1
    SAVETEXT STRFORM("{DAY},{TIME}"), "narrative-after-clock.txt", 0, 1
ENDIF

""";
        string source = File.ReadAllText(flow).ReplaceLineEndings("\n");
        const string label = "$SCENARIO_TEST_RAIN_ROOM_MAIN\n";
        if (!source.Contains(label)) throw new Exception("Missing test hook.");
        File.WriteAllText(flow, source.Replace(label, label + hook, StringComparison.Ordinal), new UTF8Encoding(true));
        Exception? failure = null;
        var thread = new Thread(() => { try { Observe(assemblyPath, gamePath, story, mode); } catch (Exception e) { failure = e; } });
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        if (!thread.Join(TimeSpan.FromSeconds(50))) throw new Exception("Narrative test timeout.");
        if (failure != null) throw failure;
    }
    static void Observe(string assemblyPath, string gamePath, string story, string mode)
    {
        string folder = Path.GetDirectoryName(assemblyPath)!;
        AssemblyLoadContext.Default.Resolving += (_, name) => File.Exists(Path.Combine(folder, name.Name + ".dll")) ?
            AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.Combine(folder, name.Name + ".dll")) : null;
        var assembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(assemblyPath);
        int step = 0; bool complete = false; Exception? failure = null;
        var clock = Stopwatch.StartNew();
        using var timer = new System.Windows.Forms.Timer { Interval = 100 };
        timer.Tick += (_, _) =>
        {
            var form = Application.OpenForms.Cast<Form>().FirstOrDefault(f => f.GetType().FullName == "MinorShift.Emuera.Forms.MainWindow");
            if (form == null) return;
            try
            {
                if (clock.Elapsed.TotalSeconds > 25) throw new Exception("Narrative UI stalled at " + step);
                var console = Get(form, "console"); if (console == null || (bool)Get(console, "IsInProcess")!) return;
                string text = string.Join('\n', ((IEnumerable)Get(console, "displayLineList")!).Cast<object>());
                void Enter(string value) => Call(console, "PressEnterKey", false, value, false);
                bool Active() => (bool)Get(console, "NarrativeActive")!;
                bool ResultMenu() => text.Contains("[1] 查看物品") && text.Contains("[4] 查看人物状态");
                bool Main() => text.Contains("[10] 查看人物状态") && text.Contains("[4] 移动");
                void Key(Keys key) => Check((bool)Call(form, "NarrativeKey", key)!, "settings/playback consumes its own key " + key);
                void Paint(bool save = true, string suffix = "")
                {
                    var canvas = (PictureBox)Get(form, "MainPicBox")!;
                    using var bitmap = new Bitmap(canvas.Width, canvas.Height); using var graphics = Graphics.FromImage(bitmap);
                    graphics.SetClip(new Rectangle(Point.Empty, canvas.Size));
                    Call(form, "mainPicBox_Paint", canvas, new PaintEventArgs(graphics, new Rectangle(Point.Empty, canvas.Size)));
                    if (save && Environment.GetEnvironmentVariable("ERA_NARRATIVE_PREVIEW") is { Length: > 0 } target)
                    {
                        string path = Path.Combine(Path.GetDirectoryName(target)!, Path.GetFileNameWithoutExtension(target) + "-" + mode + suffix + ".png");
                        bitmap.Save(path, ImageFormat.Png); Console.WriteLine("Saved narrative settings preview: " + path);
                    }
                }
                // Hidden fixture windows still need the normal paint lifecycle to
                // release the engine's timed waits, exactly as visible play does.
                if (step >= 6)
                {
                    Paint(false);
                    text = string.Join('\n', ((IEnumerable)Get(console, "displayLineList")!).Cast<object>());
                }
                switch (step)
                {
                    case 0 when text.Contains("[0] 开始新游戏"): Enter("3"); break;
                    case 1 when text.Contains("[8] 剧情显示"):
                        Check(Regex.Matches(text, @"\[(\d+)\]").Select(match => int.Parse(match.Groups[1].Value)).SequenceEqual(Enumerable.Range(1, 17).Append(0)),
                            "settings options run in visible order from 1 through 17, then zero");
                        Check(Compact(text[..text.IndexOf("[1]", StringComparison.Ordinal)]).Contains("剧情：一起显示/非流式"),
                            "display section summary includes current narrative mode and reception");
                        Check(text.Contains("一起显示") && text.Contains("非流式"), "new settings default to non-streaming instant display"); Enter("8"); break;
                    case 2 when (bool)Get(form, "NarrativeEditing")!:
                        Call(form, "SetGameWindowSize", "1280×720", false);
                        Paint(); var editor = Get(form, "narrativeEditor")!;
                        var stream = (Rectangle)Get(editor, "StreamingBounds")!;
                        Call(form, "NarrativeMouseDown", new MouseEventArgs(MouseButtons.Left, 1, stream.Left + 4, stream.Top + 4, 0));
                        Paint(true, "-stream");
                        Check((bool)Get(editor, "StreamingPreview")! && ((Rectangle)Get(editor, "Track")!).IsEmpty,
                            "streaming tab shows an unfinished placeholder without non-streaming speed controls");
                        Call(editor, "Speed", 90, true); Call(editor, "Mode", true);
                        Check(!File.Exists(Path.Combine(gamePath, "配置/narrative-display.json")), "previewing unfinished streaming does not enable it or alter saved options");
                        var nonStream = (Rectangle)Get(editor, "NonStreamingBounds")!;
                        Call(form, "NarrativeMouseDown", new MouseEventArgs(MouseButtons.Left, 1, nonStream.Left + 4, nonStream.Top + 4, 0));
                        Paint(false);
                        Check(!(bool)Get(editor, "StreamingPreview")! && !((Rectangle)Get(editor, "Track")!).IsEmpty, "non-streaming tab restores working display controls");
                        Key(Keys.Left); Key(Keys.Up);
                        Check((int)Get(Get(editor, "Settings")!, "CharactersPerSecond")! == 35, "keyboard adjusts speed like volume");
                        Call(editor, "Submit", "1"); Key(Keys.Down);
                        Check((int)Get(Get(editor, "Settings")!, "CharactersPerSecond")! == 1, "speed lower bound is one");
                        var track = (Rectangle)Get(editor, "Track")!;
                        Call(form, "NarrativeMouseDown", new MouseEventArgs(MouseButtons.Left, 1, track.Right, track.Top + track.Height / 2, 0));
                        Call(editor, "MouseUp");
                        Check((int)Get(Get(editor, "Settings")!, "CharactersPerSecond")! == 120, "actual mouse slider reaches 120");
                        Key(Keys.Right); Call(editor, "Speed", 60, true);
                        Check(!(bool)Get(Get(editor, "Settings")!, "Gradual")! && (int)Get(Get(editor, "Settings")!, "CharactersPerSecond")! == 120, "instant mode disables speed adjustments and keeps saved speed");
                        if (mode != "instant") Key(Keys.Left);
                        Paint(); Key(Keys.Escape); break;
                    case 3 when text.Contains("[8] 剧情显示"):
                        if (clock.ElapsedMilliseconds < 450) return;
                        var saved = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(gamePath, "配置/narrative-display.json")));
                        Check(saved.RootElement.GetProperty("Gradual").GetBoolean() == (mode != "instant") && saved.RootElement.GetProperty("CharactersPerSecond").GetInt32() == 120,
                            "mode and slider speed are saved outside scenario saves");
                        if (mode != "instant") Check(Compact(text[..text.IndexOf("[1]", StringComparison.Ordinal)]).Contains("剧情：逐字显示/非流式/120字/秒"),
                            "display summary refreshes to the chosen narrative speed");
                        Enter("0"); break;
                    case 4 when text.Contains("[0] 开始新游戏"): Enter("0"); break;
                    case 5 when text.Contains("剧本选择"): Call(form, "SetGameWindowSize", "640×480", false); Enter("1"); break;
                    case 6:
                        if (mode is "instant" or "natural")
                        {
                            if (!ResultMenu()) return;
                            Check(!Active() && Compact(text).Contains(Compact(story)), "instant/naturally finished story presents every paragraph");
                        }
                        else
                        {
                            if (!Active()) return;
                            var playback = Get(console, "narrative")!; int visible = (int)Get(playback, "VisibleLength")!;
                            if (visible < 90) return;
                            Check(visible < story.Length && !text.Contains("这段文字到这里结束"), "gradual display advances and hides the remainder");
                            Check(Compact(text).Contains(Compact(story[..visible])), "wrapped partial paragraphs contain the exact prefix without duplicated lines");
                            Check(((System.Windows.Forms.RichTextBox)Get(form, "richTextBox1")!).ReadOnly, "input is read-only while revealing");
                            Key(mode == "review" ? Keys.Escape : Keys.Enter);
                            Check(!Active(), "skip completes text without cancelling or sending an action");
                        }
                        break;
                    case 7:
                        if (mode == "review")
                        {
                            if (!text.Contains("[1] 采用本轮剧情与状态变化")) return;
                            Check(Compact(text).Contains(Compact(story)) && Regex.Matches(text, "这段文字到这里结束").Count == 1,
                                "Esc reveals the full proposal exactly once before any adoption choice"); Enter("0"); step = 10; return;
                        }
                        if (!ResultMenu()) return;
                        Check(!Active() && Compact(text).Contains(Compact(story)) && Regex.Matches(text, "这段文字到这里结束").Count == 1,
                            "full story remains exactly once and menu is ready after reveal"); Enter("4"); break;
                    case 8 when text.Contains("当前人物状态"): Enter("0"); break;
                    case 9 when ResultMenu():
                        Check(!Active() && Compact(text).Contains(Compact(story)), "returning from character list does not replay typing"); Enter("0"); break;
                    case 10 when Main():
                        Check(File.ReadAllText(Path.Combine(gamePath, "narrative-before.txt")) == File.ReadAllText(Path.Combine(gamePath, "narrative-after.txt")) &&
                            File.ReadAllText(Path.Combine(gamePath, "narrative-before-clock.txt")) == File.ReadAllText(Path.Combine(gamePath, "narrative-after-clock.txt")),
                            "presentation, skipping and browsing leave inventory, fatigue and time unchanged");
                        complete = true; timer.Stop(); form.Close(); return;
                    default: return;
                }
                step++; form.Hide();
            }
            catch (Exception e)
            {
                var current = Get(form, "console");
                if (current != null) Console.WriteLine("LAST DISPLAY: " + string.Join('\n', ((IEnumerable)Get(current, "displayLineList")!).Cast<object>()));
                foreach (string file in Directory.GetFiles(gamePath, "*.log")) Console.WriteLine(File.ReadAllText(file));
                failure = e; timer.Stop(); form.Close();
            }
        };
        EventHandler? idle = null; idle = (_, _) => { Application.Idle -= idle; timer.Start(); }; Application.Idle += idle;
        assembly.EntryPoint!.Invoke(null, [new string[] { "--ExeDir", gamePath }]);
        if (failure != null) throw failure;
        Check(complete, "actual native game completed " + mode + " display flow without API requests");
    }
}
