using System.Collections;
using System.Diagnostics;
using System.Drawing;
using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using System.Windows.Forms;

internal static class SceneMapWebIntegrationTests
{
    private const BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
    private static object? Get(object obj, string name) => obj.GetType().GetProperty(name, Flags)?.GetValue(obj) ?? obj.GetType().GetField(name, Flags)?.GetValue(obj);
    private static object? Call(object obj, string name, params object?[] args) => obj.GetType().GetMethod(name, Flags)!.Invoke(obj, args);
    private static void Check(bool value, string message)
    {
        if (!value) throw new Exception(message);
        Console.WriteLine("PASS WebGL map: " + message);
    }

    private static async Task<JsonElement> Script(object browser, string script)
    {
        var method = browser.GetType().GetMethod("ExecuteScriptAsync", Flags, null, [typeof(string)], null)!;
        string result = await (Task<string>)method.Invoke(browser, [script])!;
        using var doc = JsonDocument.Parse(result);
        return doc.RootElement.Clone();
    }

    private static async Task Capture(object browser, string name)
    {
        string? output = Environment.GetEnvironmentVariable("ERA_SCENE_MAP_PREVIEW");
        if (string.IsNullOrWhiteSpace(output)) return;
        Directory.CreateDirectory(output);
        var core = Get(browser, "CoreWebView2")!;
        var method = core.GetType().GetMethod("CapturePreviewAsync", Flags)!;
        var png = Enum.Parse(method.GetParameters()[0].ParameterType, "Png");
        using var stream = File.Create(Path.Combine(output, name));
        await (Task)method.Invoke(core, [png, stream])!;
    }

    private static async Task DispatchClose(object browser, Control view, string script)
    {
        try { await Script(browser, script); }
        catch (Exception) when (view.IsDisposed)
        {
            // The queued host close may dispose WebView before its script result crosses back.
            // The following action-menu step still verifies that this was a successful close.
        }
    }

    private static async Task VerifyStableLayout(object browser)
    {
        await Script(browser, """
            (()=>{
                const r=document.getElementById('era-map-study'),v=r._mapPreview;
                const detail=r.querySelector('.era-map-detail'),stage=r.querySelector('.era-map-stage');
                const site=v.world.sites.find(s=>s.id===5);
                window.__layoutProbe={description:site.description,resizes:0,bounds:[stage.clientWidth,stage.clientHeight,detail.clientHeight]};
                const p=window.__layoutProbe;
                p.observer=new ResizeObserver(()=>p.resizes++);p.observer.observe(stage);
                site.description='这是一段用来检查滚动的较长地点介绍。'.repeat(200);
                return true;
            })()
            """);
        await Task.Delay(90);
        await Script(browser, "window.__layoutProbe.resizes=0;true");
        for (int i = 0; i < 8; i++)
        {
            await Script(browser, $"document.getElementById('era-map-study')._mapPreview.select({(i % 2 == 0 ? 1 : 5)});true");
            await Task.Delay(60);
        }
        var stable = await Script(browser, """
            (()=>{
                const r=document.getElementById('era-map-study'),p=window.__layoutProbe;
                const stage=r.querySelector('.era-map-stage'),detail=r.querySelector('.era-map-detail');
                return {stable:p.resizes===0&&JSON.stringify(p.bounds)===JSON.stringify([stage.clientWidth,stage.clientHeight,detail.clientHeight]),
                    ratio:stage.getBoundingClientRect().height/detail.getBoundingClientRect().height,
                    scrollable:detail.scrollHeight>detail.clientHeight&&getComputedStyle(detail).overflowY==='scroll'};
            })()
            """);
        Check(stable.GetProperty("stable").GetBoolean(), "long and short place descriptions never resize the WebGL viewport");
        Check(Math.Abs(stable.GetProperty("ratio").GetDouble() - 7.0 / 3.0) < .03,
            "map and detail panel keep a fixed 7:3 split");
        Check(stable.GetProperty("scrollable").GetBoolean(), "long descriptions scroll inside their own fixed panel");
        await Script(browser, """
            (()=>{
                const r=document.getElementById('era-map-study'),p=window.__layoutProbe,d=r.querySelector('.era-map-detail');
                d.scrollTop=80;p.scroll=d.scrollTop;p.node=r.querySelector('.era-detail-extra').firstChild;
                p.mutations=0;p.contentObserver=new MutationObserver(records=>p.mutations+=records.length);
                p.contentObserver.observe(d,{childList:true,characterData:true,subtree:true});
                r._mapPreview.redraw();return true;
            })()
            """);
        await Task.Delay(90);
        var preserved = await Script(browser, """
            (()=>{
                const r=document.getElementById('era-map-study'),p=window.__layoutProbe,d=r.querySelector('.era-map-detail');
                const result=p.scroll>0&&d.scrollTop===p.scroll&&p.node===r.querySelector('.era-detail-extra').firstChild&&p.mutations===0;
                p.observer.disconnect();p.contentObserver.disconnect();r._mapPreview.world.sites.find(s=>s.id===5).description=p.description;
                r._mapPreview.select(1);delete window.__layoutProbe;return result;
            })()
            """);
        Check(preserved.GetBoolean(), "redrawing the same place preserves detail nodes and scroll position");
        await Task.Delay(80);
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
        if (!thread.Join(TimeSpan.FromSeconds(90))) throw new Exception("WebGL map integration test timed out.");
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
                if (watch.Elapsed.TotalSeconds > 78) throw new Exception("WebGL map stalled at step " + step + ": " + Get(form, "sceneMapFailure"));
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
                    "browsing and marking a destination preserve the saved world snapshot");
                Control? web = Get(form, "sceneMapWeb") as Control;
                bool Ready() => web != null && (bool)Get(web, "Ready")!;
                if (Map() && web == null)
                    throw new Exception("Real WebView map fell back unexpectedly: " + Get(form, "sceneMapFailure"));

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
                    await Task.Delay(250);
                    NativeMapMetrics.Write("map-open");
                    var browser = Get(web!, "Browser")!;
                    var initial = await Script(browser, "(()=>{const r=document.getElementById('era-map-study'),v=r._mapPreview;return {ready:r.dataset.ready,selected:v.selected,count:v.world.sites.length,destination:v.destination,labels:[...r.querySelectorAll('.era-location')].map(e=>e.textContent)};})()");
                    Check(initial.GetProperty("ready").GetString() == "true" && initial.GetProperty("selected").GetInt32() == 1 && initial.GetProperty("count").GetInt32() == 5,
                        "embedded WebGL starts with five actual known scenes and the saved current location");
                    Check(initial.GetProperty("destination").ValueKind == JsonValueKind.Null,
                        "opening a map does not invent a destination");
                    Check(initial.GetProperty("labels").EnumerateArray().All(label => label.GetString()!.Contains('（')),
                        "actual place labels include their local coordinates");
                    var labelLayout = await Script(browser, "(()=>{const a=[...document.querySelectorAll('.era-location')].filter(e=>!e.hidden).map(e=>e.getBoundingClientRect());return a.every((r,i)=>a.slice(i+1).every(s=>r.right<=s.left||s.right<=r.left||r.bottom<=s.top||s.bottom<=r.top));})()");
                    Check(labelLayout.GetBoolean(), "visible place labels do not overlap");
                    await VerifyStableLayout(browser);
                    await Capture(browser, "scene-web-overview.png");

                    await Script(browser, "(()=>{const b=[...document.querySelectorAll('.era-location')].find(e=>e.textContent.startsWith('村落集市'));b.dispatchEvent(new MouseEvent('mouseenter'));return true;})()");
                    await Task.Delay(120);
                    var hovered = await Script(browser, "(()=>{const r=document.getElementById('era-map-study'),v=r._mapPreview;return {hover:v.hover,heading:r.querySelector('h3').textContent,description:r.querySelector('.era-detail-description').textContent};})()");
                    var localWorld = Get(Get(form, "sceneMap")!, "world")!;
                    var market = ((IEnumerable)Get(localWorld, "KnownScenes")!).Cast<object>().Single(card => (string)Get(card, "Name")! == "村落集市");
                    Check(hovered.GetProperty("hover").GetInt32() == 5 && hovered.GetProperty("heading").GetString()!.StartsWith("村落集市（") &&
                        hovered.GetProperty("description").GetString()!.Contains((string)Get(market, "Description")!),
                        "DOM hovering focuses the market and displays its actual saved description");
                    await Script(browser, "(()=>{const b=[...document.querySelectorAll('.era-location')].find(e=>e.textContent.startsWith('村落集市'));b.click();b.dispatchEvent(new MouseEvent('mouseleave'));return true;})()");
                    await Task.Delay(100);
                    await Script(browser, "document.querySelector('.era-set-destination').click();true");
                    await Task.Delay(120);
                    var marked = await Script(browser, "(()=>{const v=document.getElementById('era-map-study')._mapPreview;return {selected:v.selected,destination:v.destination};})()");
                    Check(marked.GetProperty("selected").GetInt32() == 5 && marked.GetProperty("destination").GetProperty("siteId").GetInt32() == 5 &&
                        (string)Get(Get(form, "sceneMap")!, "CurrentLocation")! == "森林边的小屋",
                        "destination marker changes independently of the character's saved location");
                    Unchanged();
                    await Capture(browser, "scene-web-destination.png");

                    double northBefore = (await Script(browser, "Number(document.querySelector('.era-compass').dataset.northAngle)")).GetDouble();
                    await Script(browser, "(()=>{const v=document.getElementById('era-map-study')._mapPreview;v.camera.yaw+=.6;v.camera.distance*=1.2;v.redraw();return true;})()");
                    await Task.Delay(120);
                    var instrument = await Script(browser, "(()=>{const r=document.getElementById('era-map-study'),v=r._mapPreview,c=r.querySelector('canvas').getBoundingClientRect(),s=r.querySelector('.era-map-scale'),p=+s.dataset.pixels,a=v.floorPoint(c.width/2-p/2,c.height*.52),b=v.floorPoint(c.width/2+p/2,c.height*.52);return {north:+r.querySelector('.era-compass').dataset.northAngle,expected:+s.dataset.metres,measured:Math.hypot(a[0]-b[0],a[2]-b[2])*v.world.metersPerUnit};})()");
                    Check(Math.Abs(instrument.GetProperty("north").GetDouble() - northBefore) > 8,
                        "embedded compass tracks a changed 3D view");
                    double expected = instrument.GetProperty("expected").GetDouble(), measured = instrument.GetProperty("measured").GetDouble();
                    Check(expected > 0 && Math.Abs(measured / expected - 1) < .02,
                        "embedded center reference scale remains accurate after zoom and rotation");

                    var display = assembly.GetType("MinorShift.Emuera.NativeDisplay.DisplayRuntime")!;
                    var oldTheme = display.GetProperty("Current", Flags)!.GetValue(null);
                    var newTheme = Activator.CreateInstance(assembly.GetType("MinorShift.Emuera.NativeDisplay.DisplayTheme")!, 0xE7B27D, 0x141018, 0x7AD7FA)!;
                    display.GetMethod("Preview", Flags)!.Invoke(null, [console, newTheme]); Paint();
                    await Task.Delay(150);
                    var colors = await Script(browser, "(()=>{const v=document.getElementById('era-map-study')._mapPreview;return v.settings;})()");
                    Check(colors.GetProperty("foreground").GetString() == "#e7b27d" && colors.GetProperty("background").GetString() == "#141018" && colors.GetProperty("focus").GetString() == "#7ad7fa",
                        "host display settings reach the running WebGL document");
                    await Capture(browser, "scene-web-themed.png");
                    display.GetMethod("Preview", Flags)!.Invoke(null, [console, oldTheme]); Paint();
                    await Task.Delay(100);

                    form.GetType().GetField("ignoreUsageChartClicksUntil", Flags)!.SetValue(form, 0L);
                    var icon = (Rectangle)Get(form, "UsageChartButtonBounds")!;
                    Click(new(icon.Left + icon.Width / 2, icon.Top + icon.Height / 2));
                    Check((bool)Get(form, "UsageChartActive")! && Map() && !web!.Visible,
                        "native usage chart overlays and hides the WebView without closing the map");
                    Key(Keys.Escape);
                    Check(!(bool)Get(form, "UsageChartActive")! && Map() && web!.Visible,
                        "chart Escape restores the same WebView");
                    Check((await Script(browser, "document.getElementById('era-map-study')._mapPreview.selected")).GetInt32() == 5,
                        "chart round trip preserves the inspected location");
                    closedView = web;
                    await DispatchClose(browser, web!, "document.querySelector('.era-return').click();true");
                    step++;
                }
                else if (step == 4 && Main())
                {
                    Check(!Map() && Get(form, "sceneMapWeb") == null && closedView!.IsDisposed,
                        "HTML zero return releases the WebView and resumes the action menu");
                    Enter("12"); step++;
                }
                else if (step == 5 && Map() && Ready())
                {
                    var browser = Get(web!, "Browser")!;
                    var reopened = await Script(browser, "(()=>{const v=document.getElementById('era-map-study')._mapPreview;return {selected:v.selected,destination:v.destination};})()");
                    Check(reopened.GetProperty("selected").GetInt32() == 1 && reopened.GetProperty("destination").ValueKind == JsonValueKind.Null,
                        "reopening rebuilds from actual saved location without the previous browsing destination");
                    ((Control)browser).Focus();
                    closedView = web;
                    await DispatchClose(browser, web!, "document.dispatchEvent(new KeyboardEvent('keydown',{key:'Escape',code:'Escape',bubbles:true}));true");
                    step++;
                }
                else if (step == 6 && Main())
                {
                    Check(!Map() && Get(form, "sceneMapWeb") == null && closedView!.IsDisposed,
                        "Escape from the map document releases the WebView and returns once");
                    Enter("12"); step++;
                }
                else if (step == 7 && Map() && Ready())
                {
                    Check((string)Call(form, "SetGameWindowSize", "640×480", false)! == "", "host accepts 640×480");
                    await Task.Delay(150);
                    var browser = Get(web!, "Browser")!;
                    var narrow = await Script(browser, "(()=>{const b=document.querySelector('.era-return').getBoundingClientRect();return {overflow:document.documentElement.scrollWidth>innerWidth+1,returnVisible:b.bottom<=innerHeight+1&&b.top>=0};})()");
                    await Capture(browser, "scene-web-narrow.png");
                    Check(!narrow.GetProperty("overflow").GetBoolean() && narrow.GetProperty("returnVisible").GetBoolean(),
                        "small host window keeps map and return control within the viewport");
                    Unchanged();
                    form.Activate();
                    input.Focus();
                    Check(input.Focused, "native input can regain focus from WebView");
                    input.Text = "0";
                    closedView = web;
                    Key(Keys.Enter); step++;
                }
                else if (step == 8 && Main())
                {
                    Check(!Map() && Get(form, "sceneMapWeb") == null && closedView!.IsDisposed,
                        "native input zero releases the third WebView");
                    Check(File.ReadAllText(Path.Combine(gamePath, "map-returns.txt")).Trim('\uFEFF', '\r', '\n', ' ') == "3",
                        "all three return routes complete exactly once and preserve time and world state");
                    Unchanged();
                    Check(!File.Exists(Path.Combine(gamePath, "数据", "账户", "api-usage.json")), "map interaction sends no paid API request");
                    NativeMapMetrics.Write("after-three-open-close");
                    completed = true;
                    timer.Stop();
                    form.Close();
                }
            }
            catch (Exception error)
            {
                failure = error;
                timer.Stop();
                form.Close();
            }
            finally { busy = false; }
        };
        EventHandler? begin = null;
        begin = (_, _) => { Application.Idle -= begin; timer.Start(); };
        Application.Idle += begin;
        assembly.EntryPoint!.Invoke(null, [new string[] { "--ExeDir", gamePath }]);
        if (failure != null) throw failure;
        Check(completed, "actual native player completed the WebGL map lifecycle");
    }
}
