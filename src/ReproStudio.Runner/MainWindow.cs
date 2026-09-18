using System;
using System.Diagnostics;
using System.IO;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using ReproStudio.Shared;
using ReproStudio_Runner.Services;
using Windows.Graphics;

namespace ReproStudio_Runner;

/// <summary>
/// The preview window, built entirely in code (no XAML) so the runner carries no
/// WASDK version stamp. It reads a snippet file, renders it on the stage, and
/// watches the file so edits from the host hot-reload without a relaunch.
/// </summary>
public sealed class MainWindow : Window
{
    private readonly string? _requestPath;
    private readonly bool _isHeadless;
    private readonly string? _screenshotPath;
    private readonly nint _hwnd;
    private readonly ScreenshotCapture? _screenshotCapture;
    private readonly RenderEngine _engine = new();
    private readonly DispatcherQueueTimer _debounceTimer;
    private readonly ContentControl _stage;
    private readonly TextBlock _logText;
    private readonly ScrollViewer _logScroller;
    private readonly InfoBar _errorBar;
    private FileSystemWatcher? _watcher;
    private bool _topmost;
    private bool _closed;
    private long _renderGeneration;
    private CancellationTokenSource? _captureCancellation;
    private Task? _captureTask;

    public MainWindow(string? requestPath, RunnerBounds? bounds, bool isHeadless = false, string? screenshotPath = null)
    {
        _requestPath = requestPath;
        _isHeadless = isHeadless;
        _screenshotPath = screenshotPath;
        _hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        if (_isHeadless)
        {
            // Must precede even snippet Setup: it can call window.Activate itself.
            // Throw on failure so App exits without ever showing this window.
            WindowCaptureInterop.ApplyAppCloak(_hwnd);
        }

        Title = "Repro preview";

        if (bounds is RunnerBounds b)
        {
            // Place the runner where the host asked (flush to its right, same height).
            AppWindow.MoveAndResize(new RectInt32(b.X, b.Y, b.Width, b.Height));
        }

        _stage = new ContentControl
        {
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Stretch,
        };
        AutomationProperties.SetName(_stage, "Repro preview stage");

        _logText = new TextBlock
        {
            Padding = new Thickness(8),
            FontFamily = new FontFamily("Consolas"),
            FontSize = 12,
            IsTextSelectionEnabled = true,
            TextWrapping = TextWrapping.Wrap,
        };
        AutomationProperties.SetName(_logText, "Repro log");

        _logScroller = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = _logText,
        };

        _errorBar = new InfoBar
        {
            IsClosable = false,
            IsOpen = false,
            Severity = InfoBarSeverity.Error,
        };

        Content = BuildLayout();
        if (_screenshotPath is not null) _screenshotCapture = new ScreenshotCapture(_hwnd);

        _debounceTimer = DispatcherQueue.CreateTimer();
        _debounceTimer.Interval = TimeSpan.FromMilliseconds(150);
        _debounceTimer.IsRepeating = false;
        _debounceTimer.Tick += (s, e) => LoadAndRender();

        ReproApi.LogSink = AppendLog;
        Closed += (s, e) =>
        {
            _closed = true;
            _captureCancellation?.Cancel();
            _debounceTimer.Stop();
            _watcher?.Dispose();
            ReproApi.LogSink = null;
            if (_screenshotCapture is not null) _ = CloseCaptureAsync();
        };

        // Setting IsAlwaysOnTop before the window is shown doesn't stick, so re-apply
        // it whenever the window activates - this is what makes a freshly launched
        // runner honor a persisted "keep on top" choice.
        Activated += OnActivated;

        if (string.IsNullOrEmpty(_requestPath))
        {
            ShowError("startup", "No --request file was provided.");
            return;
        }

        StartWatching(_requestPath);
        LoadAndRender();
    }

    private Grid BuildLayout()
    {
        var logHeader = new TextBlock { Text = "Log", Margin = new Thickness(8, 4, 8, 2) };
        TrySetStyle(logHeader, "CaptionTextBlockStyle");

        var logBorder = new Border
        {
            BorderThickness = new Thickness(0, 1, 0, 0),
            Background = TryGetBrush("LayerFillColorDefaultBrush"),
            BorderBrush = TryGetBrush("CardStrokeColorDefaultBrush"),
            Child = _logScroller,
        };
        Grid.SetRow(logBorder, 1);

        var logGrid = new Grid { Height = 140 };
        logGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        logGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        logGrid.Children.Add(logHeader);
        logGrid.Children.Add(logBorder);
        Grid.SetRow(logGrid, 1);

        Grid.SetRow(_errorBar, 2);

        var versionText = new TextBlock
        {
            Text = GetLoadedWinUiVersion(),
            Margin = new Thickness(8, 2, 8, 4),
            IsTextSelectionEnabled = true,
            TextWrapping = TextWrapping.NoWrap,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        TrySetStyle(versionText, "CaptionTextBlockStyle");
        AutomationProperties.SetName(versionText, "Loaded WinUI version");
        Grid.SetRow(versionText, 3);

        var root = new Grid { Background = TryGetBrush("ApplicationPageBackgroundThemeBrush") };
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.Children.Add(_stage);
        root.Children.Add(logGrid);
        root.Children.Add(_errorBar);
        root.Children.Add(versionText);
        return root;
    }

    /// <summary>
    /// Reads the version of the Microsoft.ui.xaml.dll actually loaded into this
    /// process. This is the whole point of the tool - it shows which WASDK/WinUI
    /// build is really running, so a wrong overlay is obvious at a glance.
    /// </summary>
    private static string GetLoadedWinUiVersion()
    {
        const string dll = "Microsoft.ui.xaml.dll";
        try
        {
            foreach (ProcessModule module in Process.GetCurrentProcess().Modules)
            {
                if (string.Equals(module.ModuleName, dll, StringComparison.OrdinalIgnoreCase)
                    && !string.IsNullOrEmpty(module.FileName))
                {
                    return Describe(module.FileName);
                }
            }

            string local = Path.Combine(AppContext.BaseDirectory, dll);
            if (File.Exists(local))
            {
                return Describe(local);
            }
        }
#pragma warning disable CA1031 // A diagnostic readout must never take down the runner.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            CrashLog.Log("WinUI version probe failed: " + ex);
        }

        return dll + ": not loaded";

        static string Describe(string path)
        {
            FileVersionInfo info = FileVersionInfo.GetVersionInfo(path);
            return $"{dll}  {info.FileVersion}";
        }
    }

    private static Brush? TryGetBrush(string key) =>
        Application.Current.Resources.TryGetValue(key, out object value) && value is Brush brush
            ? brush
            : null;

    private static void TrySetStyle(FrameworkElement element, string key)
    {
        if (Application.Current.Resources.TryGetValue(key, out object value) && value is Style style)
        {
            element.Style = style;
        }
    }

    private void StartWatching(string path)
    {
        string? dir = Path.GetDirectoryName(path);
        string file = Path.GetFileName(path);
        if (string.IsNullOrEmpty(dir))
        {
            return;
        }

        _watcher = new FileSystemWatcher(dir, file)
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
            EnableRaisingEvents = true,
        };
        _watcher.Changed += OnRequestFileChanged;
        _watcher.Created += OnRequestFileChanged;
        _watcher.Renamed += OnRequestFileChanged;
    }

    private void OnRequestFileChanged(object sender, FileSystemEventArgs e)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            if (_closed) return;
            _captureCancellation?.Cancel();
            _debounceTimer.Stop();
            _debounceTimer.Start();
        });
    }

    private void LoadAndRender()
    {
        if (_closed || string.IsNullOrEmpty(_requestPath))
        {
            return;
        }

        Snippet? snippet = SnippetIo.TryRead(_requestPath);
        if (snippet is null)
        {
            // Mid-write or missing; the watcher fires again when the file is whole.
            return;
        }

        _captureCancellation?.Cancel();
        long generation = ++_renderGeneration;
        if (_isHeadless && !VerifyHeadlessMode()) return;
        if (_screenshotCapture is not null)
        {
            var cancellation = new CancellationTokenSource();
            _captureCancellation = cancellation;
            _captureTask = CaptureAndReportAsync(snippet, generation, cancellation, _captureTask);
        }
        else
        {
            RenderSnippet(snippet);
        }
    }

    private RunnerResult RenderSnippet(Snippet snippet)
    {
        var response = new RunnerResult { RequestId = snippet.RequestId };
        try
        {
            ClearLog();
            ApplyWindowOptions(snippet);
            RenderResult result = _engine.Render(snippet, this);
            if (result.Success)
            {
                _stage.Content = result.Root;
                ApplyHostOptions(snippet);
                HideError();
                response.RenderSucceeded = true;
            }
            else
            {
                CrashLog.Log(
                    $"Render failed ({result.Phase}): "
                    + (result.Diagnostic ?? result.Error ?? "Unknown error."));
                // Do not show an old preview underneath a screenshot of a new error.
                if (_screenshotPath is not null) _stage.Content = null;
                ShowError(result.Phase, result.Error);
                response.RenderError = $"{result.Phase}: {result.Error ?? "Unknown error."}";
            }
        }
#pragma warning disable CA1031 // Last-resort guard: a reload must never crash the runner.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            CrashLog.Log("LoadAndRender failed: " + ex);
            if (_screenshotPath is not null) _stage.Content = null;
            ShowError("runtime", ex.Message);
            response.RenderError = "runtime: " + ex.Message;
        }

        return response;
    }

    private async Task CaptureAndReportAsync(
        Snippet snippet, long generation, CancellationTokenSource cancellation, Task? previousCapture)
    {
        Guid requestId = snippet.RequestId;
        string? temporaryPath = null;
        try
        {
            // Keep a single WGC session active while scenes change. Serialize readers
            // so a superseded request cannot consume the new render's frame.
            if (previousCapture is not null) await previousCapture;
            previousCapture = null;
            cancellation.Token.ThrowIfCancellationRequested();
            await _screenshotCapture!.PrepareForRenderAsync(Content as FrameworkElement, cancellation.Token);
            if (!IsCurrentCapture(requestId, generation)) return;
            if (_isHeadless && !VerifyHeadlessMode()) return;
            RunnerResult result = RenderSnippet(snippet);
            CrashLog.Log($"Request {requestId}: render {(result.RenderSucceeded ? "succeeded" : "failed: " + result.RenderError)}; capturing.");
            Screenshot? screenshot = null;
            try
            {
                if (_isHeadless && !VerifyHeadlessMode()) return;
                if (Content is not FrameworkElement root)
                {
                    throw new InvalidOperationException("Runner Window.Content is not a FrameworkElement.");
                }

                screenshot = await _screenshotCapture.CaptureAsync(root, cancellation.Token);
            }
            catch (Exception ex) when (CaptureFailures.IsExpected(ex))
            {
                result.CaptureError = CaptureFailures.Describe(ex);
            }

            cancellation.Token.ThrowIfCancellationRequested();
            if (!IsCurrentCapture(requestId, generation)) return;
            if (screenshot is not null)
            {
                result.CaptureWarning = screenshot.Warning;
                try
                {
                    // Write bytes asynchronously to a sibling file; only the final rename
                    // and result publication run on the UI thread, without an intervening
                    // await. A newer render can never interleave with this commit.
                    temporaryPath = _screenshotPath + ".tmp-" + Guid.NewGuid().ToString("N");
                    await File.WriteAllBytesAsync(temporaryPath, screenshot.Png, cancellation.Token);
                    cancellation.Token.ThrowIfCancellationRequested();
                    if (!IsCurrentCapture(requestId, generation)) return;
                    if (_isHeadless && !VerifyHeadlessMode()) return;
                    File.Move(temporaryPath, _screenshotPath!, overwrite: true);
                    temporaryPath = null;
                    result.ScreenshotPath = _screenshotPath;
                    result.CaptureMethod = screenshot.Method;
                }
                catch (Exception ex) when (CaptureFailures.IsExpected(ex))
                {
                    // A bad output path is not a WGC failure. Never recapture with RTB.
                    result.CaptureError = "PNG output failed: " + CaptureFailures.Describe(ex);
                }
            }

            if (!IsCurrentCapture(requestId, generation)) return;
            if (!await PublishResultAsync(result, generation, cancellation.Token)) return;
            CrashLog.Log(result.CaptureError is null
                ? $"Request {requestId}: PNG saved via {result.CaptureMethod}: {result.ScreenshotPath}"
                : $"Request {requestId}: capture failed: {result.CaptureError}");
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            CrashLog.Log($"Request {requestId}: capture superseded or Runner closed; no result published.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            CrashLog.Log($"Request {requestId}: cannot publish capture result: {ex}");
        }
        finally
        {
            if (temporaryPath is not null)
            {
                try
                {
                    if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    CrashLog.Log("Cannot remove incomplete screenshot: " + ex.Message);
                }
            }

            if (ReferenceEquals(_captureCancellation, cancellation)) _captureCancellation = null;
            cancellation.Dispose();
        }
    }

    private async Task<bool> PublishResultAsync(RunnerResult result, long generation, CancellationToken cancellationToken)
    {
        for (int attempt = 0; ; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsCurrentCapture(result.RequestId, generation)) return false;
            try
            {
                RunnerResultIo.WriteAtomic(RunnerResultIo.GetPath(_requestPath!), result);
                return true;
            }
            catch (Exception ex) when (attempt < 10 && (ex is IOException or UnauthorizedAccessException))
            {
                // A polling reader without FILE_SHARE_DELETE can briefly prevent
                // atomic replacement. Retry publication only, never render/recapture.
                await Task.Delay(50, cancellationToken);
            }
        }
    }

    private async Task CloseCaptureAsync()
    {
        try
        {
            try
            {
                if (_captureTask is not null) await _captureTask;
            }
            finally
            {
                await _screenshotCapture!.CloseAsync();
            }
        }
        catch (Exception ex) when (CaptureFailures.IsExpected(ex))
        {
            CrashLog.Log("Capture resource cleanup: " + ex.Message);
        }
    }

    private bool IsCurrentCapture(Guid requestId, long generation) =>
        !_closed && generation == _renderGeneration && SnippetIo.TryRead(_requestPath!)?.RequestId == requestId;

    private bool VerifyHeadlessMode()
    {
        try
        {
            WindowCaptureInterop.VerifyAppCloak(_hwnd);
            return true;
        }
        catch (Exception ex) when (CaptureFailures.IsExpected(ex))
        {
            CrashLog.Log("Headless cloak was lost; closing Runner rather than continuing visibly: " + ex.Message);
            Environment.ExitCode = 1;
            Close();
            return false;
        }
    }

    private void ApplyHostOptions(Snippet snippet)
    {
        if (_stage.Content is not FrameworkElement fe)
        {
            return;
        }

        fe.RequestedTheme = snippet.Theme switch
        {
            "Light" => ElementTheme.Light,
            "Dark" => ElementTheme.Dark,
            _ => ElementTheme.Default,
        };

        fe.FlowDirection = string.Equals(snippet.FlowDirection, "RightToLeft", StringComparison.OrdinalIgnoreCase)
            ? FlowDirection.RightToLeft
            : FlowDirection.LeftToRight;
    }

    /// <summary>
    /// Applies window-level options that don't depend on the rendered tree, so they
    /// still take effect when the XAML fails to parse. Currently just keep-on-top.
    /// </summary>
    private void ApplyWindowOptions(Snippet snippet)
    {
        _topmost = !_isHeadless && snippet.Topmost;
        ApplyTopmost();
    }

    private void OnActivated(object sender, WindowActivatedEventArgs e)
    {
        // Activated also fires when the window *de*activates, including the deactivation
        // that happens while it is being torn down. Only re-apply on the way in.
        if (e.WindowActivationState != WindowActivationState.Deactivated)
        {
            ApplyTopmost();
        }
    }

    private void ApplyTopmost()
    {
        if (_closed)
        {
            return;
        }

        try
        {
            if (AppWindow.Presenter is OverlappedPresenter presenter)
            {
                presenter.IsAlwaysOnTop = _topmost;
            }
        }
        catch (Exception ex)
        {
            // Keep-on-top is cosmetic, and the presenter can go invalid underneath us
            // during teardown. The runner exists to survive bad states, so log and live.
            CrashLog.Log("ApplyTopmost failed (ignored): " + ex.Message);
        }
    }

    private void ShowError(string phase, string? message)
    {
        _errorBar.Title = phase + " error";
        _errorBar.Message = message ?? "Unknown error.";
        _errorBar.IsOpen = true;
    }

    private void HideError()
    {
        _errorBar.IsOpen = false;
    }

    private void AppendLog(string message)
    {
        // Mirror to the log file as well as the in-window panel. A repro's Log()
        // output is often the measurement itself, and a number you can only read
        // by looking at a window is one you can't script, capture, or paste into
        // a bug. Probes in particular are usually run on some other machine.
        CrashLog.Log(message);

        DispatcherQueue.TryEnqueue(() =>
        {
            _logText.Text = _logText.Text.Length == 0 ? message : _logText.Text + "\n" + message;
            _logScroller.UpdateLayout();
            _logScroller.ChangeView(null, _logScroller.ScrollableHeight, null, true);
        });
    }

    private void ClearLog() => _logText.Text = string.Empty;
}
