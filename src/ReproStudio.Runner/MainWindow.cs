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
    private readonly RenderEngine _engine = new();
    private readonly DispatcherQueueTimer _debounceTimer;
    private readonly ContentControl _stage;
    private readonly TextBlock _logText;
    private readonly ScrollViewer _logScroller;
    private readonly InfoBar _errorBar;
    private FileSystemWatcher? _watcher;
    private bool _topmost;
    private bool _closed;

    public MainWindow(string? requestPath, RunnerBounds? bounds)
    {
        _requestPath = requestPath;
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

        _debounceTimer = DispatcherQueue.CreateTimer();
        _debounceTimer.Interval = TimeSpan.FromMilliseconds(150);
        _debounceTimer.IsRepeating = false;
        _debounceTimer.Tick += (s, e) => LoadAndRender();

        ReproApi.LogSink = AppendLog;
        Closed += (s, e) =>
        {
            _closed = true;
            _watcher?.Dispose();
            ReproApi.LogSink = null;
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
            _debounceTimer.Stop();
            _debounceTimer.Start();
        });
    }

    private void LoadAndRender()
    {
        if (string.IsNullOrEmpty(_requestPath))
        {
            return;
        }

        Snippet? snippet = SnippetIo.TryRead(_requestPath);
        if (snippet is null)
        {
            // Mid-write or missing; the watcher fires again when the file is whole.
            return;
        }

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
            }
            else
            {
                ShowError(result.Phase, result.Error);
            }
        }
#pragma warning disable CA1031 // Last-resort guard: a reload must never crash the runner.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            CrashLog.Log("LoadAndRender failed: " + ex);
            ShowError("runtime", ex.Message);
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
        _topmost = snippet.Topmost;
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
        DispatcherQueue.TryEnqueue(() =>
        {
            _logText.Text = _logText.Text.Length == 0 ? message : _logText.Text + "\n" + message;
            _logScroller.UpdateLayout();
            _logScroller.ChangeView(null, _logScroller.ScrollableHeight, null, true);
        });
    }

    private void ClearLog() => _logText.Text = string.Empty;
}
