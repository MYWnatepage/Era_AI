using System.Collections;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.Loader;
using System.Windows.Forms;

internal static class NavigationProfileIntegrationTests
{
    private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    private static object? Get(object obj, string name) => obj.GetType().GetProperty(name, Flags)?.GetValue(obj) ?? obj.GetType().GetField(name, Flags)?.GetValue(obj);
    private static object? Call(object obj, string name, params object?[] args) => obj.GetType().GetMethod(name, Flags)!.Invoke(obj, args);
    private static void Check(bool value, string message)
    { if (!value) throw new Exception(message); Console.WriteLine("PASS navigation/profile: " + message); }

    internal static void Run(string assemblyPath, string gamePath)
    {
        Exception? failure = null;
        var thread = new Thread(() => { try { Observe(assemblyPath, gamePath); } catch (Exception error) { failure = error; } });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        if (!thread.Join(TimeSpan.FromSeconds(45))) throw new Exception("Navigation/profile UI timed out.");
        if (failure != null) throw failure;
    }

    private static void Observe(string assemblyPath, string gamePath)
    {
        string folder = Path.GetDirectoryName(assemblyPath)!;
        AssemblyLoadContext.Default.Resolving += (_, name) => File.Exists(Path.Combine(folder, name.Name + ".dll"))
            ? AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.Combine(folder, name.Name + ".dll")) : null;
        var assembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(assemblyPath);
        Exception? failure = null;
        bool completed = false;
        int step = 0;
        var watch = Stopwatch.StartNew();
        using var timer = new System.Windows.Forms.Timer { Interval = 90 };
        timer.Tick += (_, _) =>
        {
            var form = Application.OpenForms.Cast<Form>().FirstOrDefault(f => f.GetType().FullName == "MinorShift.Emuera.Forms.MainWindow");
            if (form == null) return;
            try
            {
                if (watch.Elapsed.TotalSeconds > 35) throw new Exception("Stalled at step " + step);
                var console = Get(form, "console");
                if (console == null || (bool)Get(console, "IsInProcess")!) return;
                string text = string.Join('\n', ((IEnumerable)Get(console, "displayLineList")!).Cast<object>());
                void Enter(string input) => Call(console, "PressEnterKey", false, input, false);
                void Escape() => Call(form, "richTextBox1_KeyDown", Get(form, "richTextBox1"), new KeyEventArgs(Keys.Escape));
                bool Title() => text.Contains("[0] 开始新游戏");
                bool Select() => text.Contains("剧本选择") && text.Contains("[0] 返回标题");
                bool Main() => text.Contains("[10] 查看人物状态");
                bool List() => text.Contains("当前人物状态") && text.Contains("共 2 位人物");
                bool World() => text.Contains("场景与人物所在地");
                switch (step)
                {
                    case 0 when Title():
                        Check((string)Get(Get(form, "folderSelectDialog")!, "Description")! == "请选择要重新加载的文件夹", "reload picker uses Chinese");
                        Enter("0"); break;
                    case 1 when Select(): Escape(); break;
                    case 2 when Title(): Check(true, "Esc from new-game selection returns to live title"); Enter("0"); break;
                    case 3 when Select(): Enter("0"); break;
                    case 4 when Title(): Check(true, "numeric zero from selection also returns to title"); Enter("0"); break;
                    case 5 when Select(): Escape(); break;
                    case 6 when Title(): Check(true, "repeated Escape does not exhaust or close the player"); Enter("0"); break;
                    case 7 when Select(): Enter("1"); break;
                    case 8 when Main(): Enter("10"); break;
                    case 9 when List():
                        Check(text.Contains("11岁 / 145 cm / 36 kg / 健康") && text.Contains("7岁 / 120 cm / 22 kg / 健康"), "both profiles visible on real character list");
                        Enter("1"); break;
                    case 10 when text.Contains("年龄："):
                        Check(text.Contains("11 岁") && text.Contains("145 cm") && text.Contains("36 kg") && text.Contains("身体状况：") && text.Contains("健康"), "player detail shows measurements and initial health");
                        Enter("0"); break;
                    case 11 when List(): Enter("2"); break;
                    case 12 when text.Contains("年龄："):
                        Check(text.Contains("7 岁") && text.Contains("120 cm") && text.Contains("22 kg") && text.Contains("健康"), "Ella detail shows measurements and initial health");
                        Escape(); break;
                    case 13 when List(): Escape(); break;
                    case 14 when Main(): Enter("12"); break;
                    case 15 when World():
                        Check(text.Contains("[0] 返回行动菜单") && !text.Contains("[Esc] 返回行动菜单"), "scene return label uses zero");
                        var sceneInput = (RichTextBox)Get(form, "richTextBox1")!;
                        form.Show(); form.Activate(); sceneInput.Focus();
                        Check(sceneInput.Focused, "numeric scene return uses the focused input control");
                        sceneInput.Text = "0";
                        Call(form, "richTextBox1_KeyDown", Get(form, "richTextBox1"), new KeyEventArgs(Keys.Enter)); break;
                    case 16 when Main(): Enter("12"); break;
                    case 17 when World(): Escape(); break;
                    case 18 when Main():
                        Check(true, "scene numeric zero and Escape both return to action menu");
                        completed = true; timer.Stop(); form.Close(); return;
                    default: return;
                }
                step++;
                form.Hide();
            }
            catch (Exception error) { failure = error; timer.Stop(); form.Close(); }
        };
        EventHandler? begin = null;
        begin = (_, _) => { Application.Idle -= begin; timer.Start(); };
        Application.Idle += begin;
        assembly.EntryPoint!.Invoke(null, [new string[] { "--ExeDir", gamePath }]);
        if (failure != null) throw failure;
        Check(completed, "actual native player completed navigation/profile sequence without closing unexpectedly");
    }
}
