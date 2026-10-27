using System;
using System.Drawing;
using System.IO;
using System.Text.Json;
using System.Windows.Forms;
using MinorShift.Emuera.NativeAI;
using MinorShift.Emuera.NativeDisplay;

namespace MinorShift.Emuera.Forms;

internal sealed partial class MainWindow
{
    private SceneMapPage sceneMap;
    private NativeSceneMapView sceneMapNative;
    private DisplayTheme sceneMapTheme;
    private string sceneMapFailure = "";
    private string pendingMapRoute = "";
    public string TakeMapRoute()
    {
        string route = pendingMapRoute; pendingMapRoute = "";
        if (route.Length == 0) throw new InvalidOperationException("尚未确认移动路线。");
        return route;
    }
    public void ShowMovementMap(InventoryState state, ScenarioDefinition scenario, int day, int time)
    {
        ShowSceneMap(state, scenario.World, scenario);
        if (sceneMapNative == null) { ResetSceneMap(); throw new InvalidOperationException("当前设备暂时无法打开画线路线，请返回后重试。"); }
        var planner = new NativeMapTravel(state, scenario, sceneMapNative.World, day, time);
        sceneMapNative.EnableTravel(planner.Quote, points =>
        {
            pendingMapRoute = planner.Request(points);
            richTextBox1.Clear(); ResetSceneMap();
            ignoreUsageChartClicksUntil = Environment.TickCount64 + SystemInformation.DoubleClickTime;
            ignoreMusicClipboardUntil = ignoreUsageChartClicksUntil;
            console?.PressEnterKey(false, "1", false);
            richTextBox1.Focus(); mainPicBox.Invalidate();
        }, planner.Instruction);
    }
    public bool SceneMapActive => sceneMap != null;

    public void ShowSceneMap(InventoryState state, SceneCatalog initial, ScenarioDefinition scenario = null)
    {
        if (sceneMap != null) return;
        pendingMapRoute = "";
        sceneMap = new SceneMapPage(state, initial, CloseSceneMap, scenario);
        MusicShortcuts = MusicSubmenuInput = false;
        richTextBox1.Clear();
        richTextBox1.Focus();
        sceneMapFailure = "";
        try
        {
            var theme = DisplayRuntime.Current ?? new DisplayTheme();
            var payload = SceneMapDocument.BuildPayload(state, initial, theme, scenario);
            var world = NativeSceneWorld.Build(payload);
            sceneMapNative = new NativeSceneMapView(world, sceneMap.CurrentLocation, theme,
                () => sceneMap?.Close(), id => sceneMap?.Select(id), MapInitializationFailed);
            sceneMapTheme = theme;
            mainPicBox.Controls.Add(sceneMapNative);
            mainPicBox.ClientSizeChanged += LayoutSceneMap;
            LayoutSceneMap(this, EventArgs.Empty);
            sceneMapNative.BringToFront();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or JsonException or InvalidOperationException)
        { MapInitializationFailed("立体地图资料暂时无法读取，当前使用兼容地图。"); }
        mainPicBox.Invalidate();
    }
    private void LayoutSceneMap(object sender, EventArgs args)
    {
        if (sceneMapNative == null || sceneMapNative.IsDisposed) return;
        int top = Math.Max(UsageChartButtonBounds.Bottom, MusicSpeakerBounds.Bottom) + 8;
        sceneMapNative.Bounds = new Rectangle(0, Math.Min(top, mainPicBox.Height), mainPicBox.Width, Math.Max(1, mainPicBox.Height - top));
        sceneMapNative.Visible = usageChart == null;
    }
    private void MapInitializationFailed(string message)
    {
        sceneMapFailure = message;
        DisposeNativeSceneMap();
        mainPicBox.Invalidate();
    }
    private void DisposeNativeSceneMap()
    {
        mainPicBox.ClientSizeChanged -= LayoutSceneMap;
        var old = sceneMapNative;
        sceneMapNative = null;
        sceneMapTheme = null;
        if (old != null) { mainPicBox.Controls.Remove(old); old.Dispose(); }
    }
    private void ResetSceneMap()
    {
        DisposeNativeSceneMap();
        sceneMap = null;
        sceneMapFailure = "";
    }
    private void CloseSceneMap()
    {
        if (sceneMap == null) return;
        richTextBox1.Clear();
        ResetSceneMap();
        ignoreUsageChartClicksUntil = Environment.TickCount64 + SystemInformation.DoubleClickTime;
        ignoreMusicClipboardUntil = ignoreUsageChartClicksUntil;
        // Complete the owning ERB page's INPUT, so reopening starts from the current save.
        console?.PressEnterKey(false, "0", false);
        richTextBox1.Focus();
        mainPicBox.Invalidate();
    }
    private bool DrawSceneMap(Graphics graphics)
    {
        if (sceneMap == null) return false;
        if (sceneMapNative != null)
        {
            var theme = DisplayRuntime.Current ?? new DisplayTheme();
            graphics.Clear(theme.BackgroundColor);
            if (sceneMapTheme != theme) { sceneMapTheme = theme; sceneMapNative.UpdateTheme(theme); }
            return true;
        }
        sceneMap.Draw(graphics, mainPicBox.ClientRectangle, richTextBox1.Font, DisplayRuntime.Current, Runtime.Config.Config.LineHeight);
        if (sceneMapFailure.Length > 0)
            TextRenderer.DrawText(graphics, sceneMapFailure, richTextBox1.Font, new Point(12, 8), DisplayRuntime.Current.TextColor);
        return true;
    }
    private bool SceneMapKey(Keys key)
    {
        if (sceneMap == null) return false;
        if (sceneMapNative != null)
        {
            if (key == Keys.Escape) { sceneMap.Close(); return true; }
            if (key == Keys.Enter && richTextBox1.Focused)
            {
                string input = richTextBox1.Text;
                richTextBox1.Clear();
                sceneMap.Submit(input);
                if (sceneMap != null) sceneMapNative?.Select(sceneMap.SelectedId);
                return true;
            }
            return false;
        }
        if (key == Keys.Escape) sceneMap.Close();
        else if (key == Keys.Enter)
        {
            string text = richTextBox1.Text;
            richTextBox1.Clear();
            sceneMap.Submit(text);
        }
        else if (key is Keys.Left or Keys.Right) sceneMap.Step(key == Keys.Left ? -1 : 1);
        else if (key is Keys.PageUp or Keys.PageDown) sceneMap.ChangePage(key == Keys.PageUp ? -1 : 1);
        else if (key is Keys.Up or Keys.Down)
            sceneMap.Wheel(new Point(sceneMap.DetailBounds.Left, sceneMap.DetailBounds.Top), key == Keys.Up ? 120 : -120);
        else return false;
        mainPicBox.Invalidate();
        return true;
    }
}
