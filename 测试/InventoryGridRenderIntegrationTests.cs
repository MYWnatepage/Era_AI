using System.Collections;
using System.Diagnostics;
using System.Drawing;
using System.Reflection;
using System.Runtime.Loader;
using System.Text;
using System.Windows.Forms;

// Observe the actual native bridge output, not a duplicated grid renderer.
internal static class InventoryGridRenderIntegrationTests
{
    private const BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
    private static object? Get(object obj, string name) => obj.GetType().GetProperty(name, Flags)?.GetValue(obj) ?? obj.GetType().GetField(name, Flags)?.GetValue(obj);
    private static object? Call(object obj, string name, params object?[] args) => obj.GetType().GetMethod(name, Flags)!.Invoke(obj, args);
    private static void Check(bool condition, string message)
    { if (!condition) throw new Exception(message); Console.WriteLine("PASS inventory render: " + message); }

    internal static void Run(string assemblyPath, string gamePath)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { Observe(assemblyPath, gamePath); }
            catch (Exception error) { failure = error; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        if (!thread.Join(TimeSpan.FromSeconds(45))) throw new Exception("Inventory native render test timed out.");
        if (failure != null) throw failure;
    }

    private static void Observe(string assemblyPath, string gamePath)
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
        string previous = "";
        var elapsed = Stopwatch.StartNew();
        using var timer = new System.Windows.Forms.Timer { Interval = 70 };
        timer.Tick += (_, _) =>
        {
            var form = Application.OpenForms.Cast<Form>().FirstOrDefault(f => f.GetType().FullName == "MinorShift.Emuera.Forms.MainWindow");
            if (form == null) return;
            try
            {
                if (elapsed.Elapsed.TotalSeconds > 35) throw new Exception("Native grid fixture did not reach its next stage.");
                var console = Get(form, "console");
                string marker = Path.Combine(gamePath, "grid-stage.txt");
                if (console == null || (bool)Get(console, "IsInProcess")! || !File.Exists(marker)) return;
                string stage = File.ReadAllText(marker).Trim('\uFEFF', '\r', '\n', ' ');
                if (stage == previous) return;
                previous = stage;
                form.Hide();
                void Continue() => Call(console, "PressEnterKey", false, "", false);
                void Resize(string size)
                {
                    string error = (string)Call(form, "SetGameWindowSize", size, false)!;
                    Check(error == "", "disposable player accepts " + size);
                }
                int[] Metadata() => File.ReadAllText(Path.Combine(gamePath, "grid-data.txt"))
                    .Trim('\uFEFF', '\r', '\n', ' ').Split('|').Select(int.Parse).ToArray();
                if (stage == "setup")
                {
                    Resize("640×480"); Continue(); return;
                }
                int[] data = Metadata(); // total pages, columns, corrected current page
                var cells = Cells(console);
                Console.WriteLine("GRID " + stage + " metadata=" + string.Join(',', data) +
                    " canvas=" + ((PictureBox)Get(form, "MainPicBox")!).Width + " rendered=" + cells.Count);
                Check(cells.Count > 0 && cells.Count <= data[1] * 4, "native bridge emits only the current matrix page");
                if (stage == "narrow-first")
                {
                    Check(data.SequenceEqual(new[] { 4, 2, 0 }), "640px canvas uses two columns and four pages");
                    AssertCells(cells, data[1]);
                    AssertSelection(player, console, form, cells);
                    Continue(); return;
                }
                if (stage == "narrow-last")
                {
                    Check(data.SequenceEqual(new[] { 4, 2, 3 }) && cells.Select(c => c.Id).SequenceEqual(new[] { 25 }),
                        "last narrow page has the actual remaining item");
                    AssertSelection(player, console, form, cells);
                    Resize("1280×720"); Continue(); return;
                }
                if (stage == "wide-clamped")
                {
                    Check(data[1] >= 3 && data[0] < 4 && data[2] == data[0] - 1,
                        "bridge returns corrected page after widening the canvas");
                    int first = data[2] * data[1] * 4 + 1;
                    Check(cells.Select(c => c.Id).SequenceEqual(Enumerable.Range(first, 26 - first)),
                        "corrected page metadata and actual rendered items agree");
                    AssertCells(cells, data[1]);
                    AssertSelection(player, console, form, cells);
                    completed = true; timer.Stop(); Continue(); form.Close(); return;
                }
                throw new Exception("Unknown native grid fixture stage: " + stage);
            }
            catch (Exception error) { failure = error; timer.Stop(); form.Close(); }
        };
        EventHandler? begin = null;
        begin = (_, _) => { Application.Idle -= begin; timer.Start(); };
        Application.Idle += begin;
        player.EntryPoint!.Invoke(null, [new string[] { "--ExeDir", gamePath }]);
        if (failure != null) throw failure;
        Check(completed && File.Exists(Path.Combine(gamePath, "grid-pass.txt")), "real player render sequence completed without an API request");
    }

    private sealed record Cell(int Id, int X, int Width, object Part);
    private static List<Cell> Cells(object console)
    {
        var result = new List<Cell>();
        foreach (var line in (IEnumerable)Get(console, "displayLineList")!)
            foreach (var button in (IEnumerable)Get(line, "Buttons")!)
            {
                if (Get(button, "Inputs") is not string payload || !payload.StartsWith("@item:", StringComparison.Ordinal)) continue;
                var part = ((IEnumerable)Get(button, "StrArray")!).Cast<object>().Single(p => Get(p, "StringStyle") != null);
                result.Add(new(int.Parse(payload[6..]), (int)Get(button, "PointX")!, (int)Get(button, "Width")!, part));
            }
        return result;
    }

    private static void AssertCells(List<Cell> cells, int columns)
    {
        var firstRow = cells.Take(columns).ToArray();
        foreach (var (cell, index) in cells.Select((cell, index) => (cell, index)))
        {
            Check(cell.X == firstRow[index % columns].X, "row column starts stay aligned for item " + cell.Id);
            if (index % columns + 1 < columns && index + 1 < cells.Count)
                Check(cell.X + cell.Width < cells[index + 1].X, "item labels retain a nonclickable gap");
        }
    }

    private static void AssertSelection(Assembly player, object console, Form form, List<Cell> cells)
    {
        var selectedIds = new HashSet<int> { 2, 5, 17, 25 };
        void Validate()
        {
            foreach (var cell in cells)
            {
                object style = Get(cell.Part, "StringStyle")!;
                Check((bool)Get(style, "ContextSelected")! == selectedIds.Contains(cell.Id),
                    "persistent selection flag matches item " + cell.Id);
                string label = (string)Get(cell.Part, "Text")!;
                Check(!label.EnumerateRunes().Any(rune => rune.Value == 0xFFFD), "native width trimming preserves Unicode for item " + cell.Id);
            }
        }
        Validate();
        Call(console, "LeaveMouse");
        var picture = (PictureBox)Get(form, "MainPicBox")!;
        using var bitmap = new Bitmap(picture.Width, picture.Height);
        using var graph = Graphics.FromImage(bitmap);
        // A real PaintEvent supplies a finite clip; Graphics.FromImage starts with an infinite one.
        graph.SetClip(picture.ClientRectangle);
        Call(form, "mainPicBox_Paint", picture, new PaintEventArgs(graph, picture.ClientRectangle));
        if (cells[0].Id == 1 && Environment.GetEnvironmentVariable("ERA_AI_GRID_PREVIEW") is string preview)
            bitmap.Save(preview);
        Validate();
        var runtime = player.GetType("MinorShift.Emuera.NativeDisplay.DisplayRuntime")!;
        Color? selected = null, plain = null;
        foreach (var cell in cells)
        {
            object style = Get(cell.Part, "StringStyle")!;
            bool flagged = (bool)Get(style, "ContextSelected")!;
            var resolved = (Color)runtime.GetMethod("ResolveText", Flags)!.Invoke(null, [Get(style, "Color"), flagged])!;
            if (flagged) selected = resolved; else plain = resolved;
        }
        Check(selected != null && (plain == null || selected.Value.ToArgb() != plain.Value.ToArgb()),
            "chosen items keep the selection color after mouse leave and repaint");
    }
}
