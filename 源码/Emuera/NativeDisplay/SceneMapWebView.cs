#nullable enable
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace MinorShift.Emuera.NativeDisplay;

// This control owns presentation only. Its small message bridge cannot execute a game action.
internal sealed class SceneMapWebView : UserControl
{
    private static readonly object EnvironmentLock = new();
    private static readonly Dictionary<string, Task<CoreWebView2Environment>> Environments =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly string document;
    private readonly string cacheDirectory;
    private readonly Action close;
    private readonly Action<int> select;
    private readonly Action<string> failed;
    private readonly WebView2 browser;
    private readonly Label loading;
    private readonly System.Windows.Forms.Timer readyTimeout;
    private CoreWebView2? core;
    private DisplayTheme theme;
    private Task? initialization;
    private bool dead;
    private bool closing;
    private bool failureReported;
    private ulong documentNavigationId;
    private int traceCount;
    private bool awaitingInitialDocument;
    private string? acceptedDocumentUri;

    internal bool Ready { get; private set; }
    internal WebView2 Browser => browser;
    internal string FailureReason { get; private set; } = "";

    internal SceneMapWebView(string document, string cacheDirectory, DisplayTheme theme,
        Action close, Action<int> select, Action<string> failed)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(document);
        ArgumentException.ThrowIfNullOrWhiteSpace(cacheDirectory);
        this.document = document;
        this.cacheDirectory = Path.GetFullPath(cacheDirectory);
        this.theme = theme.Safe();
        this.close = close;
        this.select = select;
        this.failed = failed;

        BackColor = this.theme.BackgroundColor;
        browser = new WebView2
        {
            Dock = DockStyle.Fill,
            DefaultBackgroundColor = this.theme.BackgroundColor,
            TabStop = true
        };
        loading = new Label
        {
            Dock = DockStyle.Fill,
            Text = "正在加载地图…",
            TextAlign = ContentAlignment.MiddleCenter,
            BackColor = this.theme.BackgroundColor,
            ForeColor = this.theme.TextColor
        };
        Controls.Add(browser);
        Controls.Add(loading);
        loading.BringToFront();
        // The WinForms wrapper dispatches controller accelerators as standard key events.
        browser.KeyDown += BrowserKeyDown;
        readyTimeout = new System.Windows.Forms.Timer { Interval = 20_000 };
        readyTimeout.Tick += ReadyTimedOut;
    }

    internal Task InitializeAsync() => initialization ??= InitializeCoreAsync();

    private async Task InitializeCoreAsync()
    {
        try
        {
            if (dead || IsDisposed) return;
            Directory.CreateDirectory(cacheDirectory);
            var environment = await GetEnvironmentAsync(cacheDirectory);
            if (dead || IsDisposed) return;
            await browser.EnsureCoreWebView2Async(environment);
            if (dead || IsDisposed || browser.IsDisposed) return;

            core = browser.CoreWebView2;
            var settings = core.Settings;
            settings.AreDefaultContextMenusEnabled = false;
            settings.AreDevToolsEnabled = false;
            settings.IsStatusBarEnabled = false;
            settings.AreBrowserAcceleratorKeysEnabled = false;
            settings.IsZoomControlEnabled = false;
            settings.AreHostObjectsAllowed = false;
            settings.IsWebMessageEnabled = true;
            core.NavigationStarting += NavigationStarting;
            core.NavigationCompleted += NavigationCompleted;
            core.NewWindowRequested += NewWindowRequested;
            core.PermissionRequested += PermissionRequested;
            core.WebMessageReceived += WebMessageReceived;
            core.ProcessFailed += ProcessFailed;
            // The document is entirely embedded. Nothing in this map needs a network or file request.
            core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
            core.WebResourceRequested += WebResourceRequested;
            readyTimeout.Start();
            awaitingInitialDocument = true;
            browser.NavigateToString(document);
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            if (dead || IsDisposed) return;
            Trace("Initialize " + error.GetType().Name + " HRESULT=" + error.HResult.ToString("X8"));
            ReportFailure(error is WebView2RuntimeNotFoundException
                ? "未找到地图所需的 WebView2 运行环境，已保留原地图页面。"
                : "立体地图暂时无法加载，已保留原地图页面。");
        }
    }

    private static async Task<CoreWebView2Environment> GetEnvironmentAsync(string directory)
    {
        Task<CoreWebView2Environment> task;
        lock (EnvironmentLock)
        {
            if (!Environments.TryGetValue(directory, out task!))
            {
                task = CoreWebView2Environment.CreateAsync(userDataFolder: directory);
                Environments.Add(directory, task);
            }
        }
        try { return await task; }
        catch
        {
            lock (EnvironmentLock)
                if (Environments.TryGetValue(directory, out var cached) && ReferenceEquals(cached, task))
                    Environments.Remove(directory);
            throw;
        }
    }

    internal void Select(int id)
    {
        if (id <= 0) return;
        Post(new { type = "select", id });
    }

    internal void UpdateTheme(DisplayTheme value)
    {
        theme = value.Safe();
        BackColor = loading.BackColor = theme.BackgroundColor;
        loading.ForeColor = theme.TextColor;
        if (!browser.IsDisposed) browser.DefaultBackgroundColor = theme.BackgroundColor;
        PostTheme();
    }

    private void PostTheme() => Post(new
    {
        type = "theme",
        palette = new
        {
            background = Hex(theme.BackgroundColor),
            foreground = Hex(theme.TextColor),
            focus = Hex(ColorSafety.Selection(theme))
        }
    });

    private static string Hex(Color color) => $"#{color.R:x2}{color.G:x2}{color.B:x2}";

    private void Post(object value)
    {
        if (dead || IsDisposed || !Ready || core == null) return;
        try { core.PostWebMessageAsJson(JsonSerializer.Serialize(value)); }
        catch (Exception error) when (error is InvalidOperationException or COMException)
        { ReportFailure("立体地图已停止响应，已保留原地图页面。"); }
    }

    private void WebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        if (dead || closing || failureReported || !IsTrustedDocument(e.Source)) return;
        try
        {
            string json = e.WebMessageAsJson;
            if (json.Length > 4096) return;
            using var message = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 8 });
            var root = message.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String) return;
            switch (type.GetString())
            {
                case "ready":
                    readyTimeout.Stop();
                    awaitingInitialDocument = false;
                    Ready = true;
                    loading.Visible = false;
                    PostTheme();
                    break;
                case "error":
                    ReportFailure("立体地图暂时无法显示，已保留原地图页面。");
                    break;
                case "close":
                    QueueClose();
                    break;
                case "select" when Ready && root.TryGetProperty("id", out var id) && id.TryGetInt32(out int selected) && selected > 0:
                    // The owner still validates this ID against its detached known-scene list.
                    select(selected);
                    break;
            }
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or COMException)
        {
            // Invalid page messages are ignored; they never reach the game input path.
        }
    }

    private static bool IsEmbeddedDocument(string uri) =>
        string.Equals(uri, "about:blank", StringComparison.OrdinalIgnoreCase);

    private bool IsTrustedDocument(string uri) => IsEmbeddedDocument(uri) ||
        acceptedDocumentUri != null && string.Equals(uri, acceptedDocumentUri, StringComparison.Ordinal);

    private static bool IsHtmlDataDocument(string uri) =>
        uri.StartsWith("data:text/html", StringComparison.OrdinalIgnoreCase) &&
        uri.Length > 14 && uri[14] is ';' or ',';

    private static string DescribeUri(string value) => IsEmbeddedDocument(value) ? "about:blank" :
        Uri.TryCreate(value, UriKind.Absolute, out var uri) ? uri.Scheme + ":" : "non-absolute";

    private void NavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
    {
        bool initialData = awaitingInitialDocument && acceptedDocumentUri == null && !e.IsUserInitiated && IsHtmlDataDocument(e.Uri);
        if (initialData)
        {
            // Some Runtime versions expose NavigateToString as a synthetic data: navigation.
            // Capture only the initial host-triggered document, never a subsequent data URL.
            acceptedDocumentUri = e.Uri;
            awaitingInitialDocument = false;
        }
        e.Cancel = !IsEmbeddedDocument(e.Uri) && !initialData;
        if (!e.Cancel) documentNavigationId = e.NavigationId;
        Trace("NavigationStarting allow=" + !e.Cancel + " uri-kind=" + DescribeUri(e.Uri) + " id=" + e.NavigationId);
    }

    private void NavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        Trace("NavigationCompleted success=" + e.IsSuccess + " status=" + e.WebErrorStatus + " id=" + e.NavigationId);
        if (!dead && !e.IsSuccess && (documentNavigationId == 0 || e.NavigationId == documentNavigationId))
            ReportFailure("立体地图页面未能加载（" + e.WebErrorStatus + "），已保留原地图页面。");
    }

    private static void NewWindowRequested(object? sender, CoreWebView2NewWindowRequestedEventArgs e) => e.Handled = true;
    private static void PermissionRequested(object? sender, CoreWebView2PermissionRequestedEventArgs e) => e.State = CoreWebView2PermissionState.Deny;

    private void WebResourceRequested(object? sender, CoreWebView2WebResourceRequestedEventArgs e)
    {
        // NavigateToString can expose its own synthetic document through this filter.
        // Let that document through; only attempted external resources are refused.
        if (IsTrustedDocument(e.Request.Uri))
        {
            Trace("WebResourceRequested allow=True uri-kind=" + DescribeUri(e.Request.Uri) + " context=" + e.ResourceContext);
            return;
        }
        Trace("WebResourceRequested allow=False uri-kind=" + DescribeUri(e.Request.Uri) + " context=" + e.ResourceContext);
        if (!dead && core != null)
            e.Response = core.Environment.CreateWebResourceResponse(Stream.Null, 403, "Forbidden", "Content-Type: text/plain");
    }

    private void Trace(string value)
    {
        // Bounded diagnostics contain event types and URI schemes only, never document/URL contents.
        if (traceCount++ >= 24) return;
        try
        {
            string path = Path.Combine(cacheDirectory, "map-host.log");
            if (File.Exists(path) && new FileInfo(path).Length > 32_768) File.WriteAllText(path, "");
            File.AppendAllText(path, DateTime.UtcNow.ToString("O") + " " + value + Environment.NewLine);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }

    private void ProcessFailed(object? sender, CoreWebView2ProcessFailedEventArgs e)
    {
        if (!dead) ReportFailure("立体地图已停止响应，已保留原地图页面。");
    }

    private void ReadyTimedOut(object? sender, EventArgs e)
    {
        readyTimeout.Stop();
        if (!Ready && !dead) ReportFailure("立体地图加载超时，已保留原地图页面。");
    }

    private void BrowserKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.KeyCode != Keys.Escape) return;
        e.Handled = true;
        e.SuppressKeyPress = true;
        QueueClose();
    }

    private void QueueClose()
    {
        if (dead || closing) return;
        closing = true;
        Queue(() => { if (!dead) close(); });
    }

    private void ReportFailure(string message)
    {
        if (dead || failureReported) return;
        failureReported = true;
        awaitingInitialDocument = false;
        readyTimeout.Stop();
        Ready = false;
        FailureReason = message;
        loading.Text = message;
        loading.Visible = true;
        Queue(() => { if (!dead) failed(message); });
    }

    private void Queue(Action callback)
    {
        if (dead || IsDisposed || !IsHandleCreated) return;
        try { BeginInvoke(callback); }
        catch (InvalidOperationException) { }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && !dead)
        {
            dead = true;
            Ready = false;
            readyTimeout.Stop();
            readyTimeout.Tick -= ReadyTimedOut;
            readyTimeout.Dispose();
            browser.KeyDown -= BrowserKeyDown;
            try
            {
                if (core != null)
                {
                    core.NavigationStarting -= NavigationStarting;
                    core.NavigationCompleted -= NavigationCompleted;
                    core.NewWindowRequested -= NewWindowRequested;
                    core.PermissionRequested -= PermissionRequested;
                    core.WebMessageReceived -= WebMessageReceived;
                    core.ProcessFailed -= ProcessFailed;
                    core.WebResourceRequested -= WebResourceRequested;
                }
            }
            catch (Exception error) when (error is InvalidOperationException or COMException) { }
            finally
            {
                core = null;
                acceptedDocumentUri = null;
                awaitingInitialDocument = false;
                browser.Dispose();
            }
        }
        base.Dispose(disposing);
    }
}
