using System.Collections;
using System.Diagnostics;
using System.Drawing;
using System.Reflection;
using System.Runtime.Loader;
using System.Text;
using System.Windows.Forms;

// Observe the actual native bridge output, not a duplicated grid renderer.
internal static class ItemQuantityInteractionTests
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
        int step = 0;
        bool timelineVerified = false;
        var elapsed = Stopwatch.StartNew();
        using var timer = new System.Windows.Forms.Timer { Interval = 100 };
        timer.Tick += (_, _) =>
        {
            var form = Application.OpenForms.Cast<Form>().FirstOrDefault(f => f.GetType().FullName == "MinorShift.Emuera.Forms.MainWindow");
            if (form == null) return;
            try
            {
                if (elapsed.Elapsed.TotalSeconds > 35) throw new Exception("Quantity interaction timed out at step " + step);
                var console = Get(form, "console");
                if (console == null || (bool)Get(console, "IsInProcess")!) return;
                if (File.Exists(Path.Combine(gamePath, "quantity-pass.txt")))
                { completed = true; timer.Stop(); form.Close(); return; }
                var cells = Cells(console);
                if (!timelineVerified)
                {
                    foreach (var line in (IEnumerable)Get(console, "displayLineList")!)
                    foreach (var button in (IEnumerable)Get(line, "Buttons")!)
                    {
                        var parts = ((IEnumerable)Get(button, "StrArray")!).Cast<object>().ToArray();
                        var slot = parts.FirstOrDefault(part => part.GetType().Name == "TimeSlotsPart");
                        if (slot == null) continue;
                        Check(parts.Length > 1, "actual time bar shares a fragment with its text prefix");
                        line.GetType().GetField("bitmapCacheEnabled", Flags)!.SetValue(line, true);
                        int width = (int)Get(button, "Width")! + 8;
                        using var bitmap = new Bitmap(width, 32);
                        using var graph = Graphics.FromImage(bitmap);
                        graph.Clear(Color.Black);
                        var draw = button.GetType().GetMethod("DrawTo", Flags)!;
                        draw.Invoke(button, [graph, 0, false, Enum.ToObject(draw.GetParameters()[3].ParameterType, 0)]);
                        float stride=(float)Get(slot,"step")!, skew=(float)Get(slot,"skew")!;
                        int x=(int)Get(slot,"PointX")! + (int)(float)Get(slot,"leading")!;
                        int centerY=(int)Math.Round((float)Get(slot,"center")!);
                        for(int i=0;i<8;i++)
                        {
                            var pixel=bitmap.GetPixel(x+(int)(1+i*stride+(stride-5+skew)/2),centerY);
                            Check((pixel.R+pixel.G+pixel.B>30)==(i<3), "cached mixed line renders time slot "+i);
                        }
                        (int Top,int Bottom) Ink(int start,int end)
                        {
                            int top=32,bottom=-1;
                            for(int yy=0;yy<32;yy++) for(int xx=start;xx<end;xx++)
                            {var c=bitmap.GetPixel(xx,yy);if(c.R+c.G+c.B>48){top=Math.Min(top,yy);bottom=Math.Max(bottom,yy);}}
                            return(top,bottom);
                        }
                        var textInk=Ink(0,(int)Get(slot,"PointX")!);
                        var slotsInk=Ink(x,bitmap.Width);
                        Check(textInk.Bottom>=textInk.Top && slotsInk.Bottom>=slotsInk.Top &&
                            Math.Abs((textInk.Top+textInk.Bottom-slotsInk.Top-slotsInk.Bottom)/2f)<=1.5f,
                            "visible time slots and visible text share the same vertical center");
                        Check((float)Get(slot,"leading")!>=5,"time slots keep space after the text separator");
                        string? path=Environment.GetEnvironmentVariable("ERA_QUANTITY_PREVIEW_DIR");
                        if(path!=null){Directory.CreateDirectory(path);bitmap.Save(Path.Combine(path,"timebar-mixed-line-preview.png"));}
                        timelineVerified=true;
                        Call(console,"PressEnterKey",false,"",false);
                        return;
                    }
                    return;
                }
                if (cells.Count == 0) return;
                form.Hide();
                var picture = (PictureBox)Get(form, "MainPicBox")!;
                var input = (RichTextBox)Get(form, "TextBox")!;
                int id = step < 8 ? 5 : 2;
                var cell = cells.Single(c => c.Id == id);
                Point Locate()
                {
                    for (int y = 1; y < picture.Height; y += 2)
                    {
                        var point = new Point(cell.X + 8, y);
                        Call(console, "MoveMouse", point);
                        if ((string?)Get(console, "SelectedString") == "@item:" + id)
                        { Call(form, "mainPicBox_MouseMove", picture, new MouseEventArgs(MouseButtons.None, 0, point.X, point.Y, 0)); return point; }
                    }
                    throw new Exception("Cannot locate item button " + id);
                }
                bool Flag() => (bool)Get(Get(cell.Part, "StringStyle")!, "ContextSelected")!;
                string Label() => (string)Get(cell.Part, "Text")!;
                void Expect(int count, bool selected)
                { Check(Flag() == selected && Label().EndsWith("：" + count), "quantity/selection step " + step); }
                void Mouse(MouseButtons button)
                { var p = Locate(); Call(form, "mainPicBox_MouseDown", picture, new MouseEventArgs(button, 1, p.X, p.Y, 0)); }
                void Key(Keys key)
                { Locate(); var e = new KeyEventArgs(key); Call(form, "richTextBox1_KeyDown", input, e); Check(e.SuppressKeyPress, "hover arrows own the key"); }
                if (step == 0)
                {
                    var slotType = player.GetType("MinorShift.Emuera.NativeDisplay.TimeSlotsPart")!;
                    var slots = Activator.CreateInstance(slotType, Flags, null, new object[]{3,8}, null)!;
                    float width = (float)Get(slots, "WidthF")!;
                    Check(width >= 160, "eight time slots have a larger fixed vector size");
                    using var bitmap = new Bitmap((int)Math.Ceiling(width)+4, 32);
                    using var graphics = Graphics.FromImage(bitmap);
                    graphics.Clear(Color.Black);
                    var method = slotType.GetMethod("DrawTo", Flags)!;
                    method.Invoke(slots, [graphics, 0, false, false, false, Enum.ToObject(method.GetParameters()[5].ParameterType, 0), false]);
                    float height = (float)Get(slots, "height")!, stride = (float)Get(slots, "step")!, skew = (float)Get(slots, "skew")!, leading = (float)Get(slots, "leading")!;
                    int centerY=(int)Math.Round((float)Get(slots,"center")!);
                    for(int i=0;i<8;i++)
                    {
                        var pixel=bitmap.GetPixel((int)(leading+1+i*stride+(stride-5+skew)/2), centerY);
                        Check((pixel.R+pixel.G+pixel.B>30)==(i<3), "filled/empty parallelogram slot "+i);
                    }
                    string? preview=Environment.GetEnvironmentVariable("ERA_QUANTITY_PREVIEW_DIR");
                    if(preview!=null){Directory.CreateDirectory(preview);bitmap.Save(Path.Combine(preview,"quantity-timebar-preview.png"));}
                    Expect(81, false); input.Text = "还没有发送的行动"; Mouse(MouseButtons.Left);
                }
                else
                {
                    if (step < 11) Check(input.Text == "还没有发送的行动", "quantity gestures preserve unfinished input");
                    switch(step)
                    {
                        case 1: Expect(1, true); Mouse(MouseButtons.Left); break;
                        case 2: Expect(2, true); Key(Keys.Up); break;
                        case 3: Expect(12, true); Key(Keys.Down); break;
                        case 4: Expect(2, true); Mouse(MouseButtons.Right); break;
                        case 5: Expect(1, true); Mouse(MouseButtons.Right); break;
                        case 6: Expect(81, false); Mouse(MouseButtons.Left); break;
                        case 7: Expect(1, true); Key(Keys.Down); break;
                        case 8: Expect(10, false); Mouse(MouseButtons.Left); break;
                        case 9: Expect(1, true); Key(Keys.Up); break;
                        case 10: Expect(10, true); Mouse(MouseButtons.Left); break;
                        case 11: Expect(10, true); Key(Keys.Down); break;
                        case 12:
                            Expect(10, false); input.Text = "0";
                            Call(form, "richTextBox1_KeyDown", input, new KeyEventArgs(Keys.Return)); break;
                        default: throw new Exception("Unexpected extra interaction");
                    }
                }
                step++;
            }
            catch (Exception error) { failure = error; timer.Stop(); form.Close(); }
        };
        EventHandler? begin = null;
        begin = (_, _) => { Application.Idle -= begin; timer.Start(); };
        Application.Idle += begin;
        player.EntryPoint!.Invoke(null, [new string[] { "--ExeDir", gamePath }]);
        if (failure != null) throw failure;
        Check(completed && File.Exists(Path.Combine(gamePath, "quantity-pass.txt")), "real player render sequence completed without an API request");
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
