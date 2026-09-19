using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using ReproStudio.Shared;
using ReproStudio_Runner.Services;

namespace ReproStudio_Runner;

public sealed partial class MainWindow
{
    private readonly AppBarToggleButton _pinButton = new() { Icon = new SymbolIcon(Symbol.Pin), Label = "Pin", IsEnabled = false };
    private readonly AppBarButton _codeButton = new() { Icon = new SymbolIcon(Symbol.Edit), Label = "Open in VS Code" };
    private readonly AppBarButton _runtimeButton = new() { Icon = new SymbolIcon(Symbol.Refresh), Label = "Runtime" };
    private readonly InfoBar _toolbarErrorBar = new() { IsClosable = true, Severity = InfoBarSeverity.Error };
    private readonly CancellationTokenSource _toolbarLifetime = new();
    private DispatcherQueueTimer? _hostTimer;
    private Snippet? _toolbarSnippet;
    private string? _preferencesPath;
    private bool _preferencesInitialized;
    private RuntimeDialog? _runtimeDialog;

    private CommandBar BuildToolbar()
    {
        SetControlIdentity(_pinButton, "RunnerPin", "Keep Runner on top");
        SetControlIdentity(_codeButton, "RunnerOpenCode", "Open original repro in Visual Studio Code");
        SetControlIdentity(_runtimeButton, "RunnerRuntime", "Choose SDK / API and native runtime, then restart Runner");
        ToolTipService.SetToolTip(_pinButton, "Keep this window above other windows. Remembered across launches; never changes the repro.");
        _pinButton.Click += PinClickedAsync;
        _codeButton.Click += CodeClickedAsync;
        _runtimeButton.Click += RuntimeClickedAsync;
        var bar = new CommandBar { DefaultLabelPosition = CommandBarDefaultLabelPosition.Right };
        AutomationProperties.SetAutomationId(bar, "RunnerToolbar");
        bar.PrimaryCommands.Add(_pinButton);
        bar.PrimaryCommands.Add(_codeButton);
        bar.PrimaryCommands.Add(_runtimeButton);
        _hostTimer = DispatcherQueue.CreateTimer();
        _hostTimer.Interval = TimeSpan.FromSeconds(1);
        _hostTimer.Tick += HostTimerTick;
        _hostTimer.Start();
        return bar;
    }

    internal static void SetControlIdentity(DependencyObject control, string id, string name)
    {
        AutomationProperties.SetAutomationId(control, id);
        AutomationProperties.SetName(control, name);
    }

    private void UpdateToolbar(Snippet snippet)
    {
        _toolbarSnippet = snippet;
        ToolTipService.SetToolTip(_codeButton, snippet.SourcePath ?? "Original source path unavailable; run with a current host.");
        _apiText.Text = "API: " + LoadedSdk.Describe(snippet);
        UpdateRuntimeAvailability();
        if (!_preferencesInitialized)
        {
            _preferencesInitialized = true;
            _preferencesPath = snippet.PreferencesPath;
            _ = LoadPinAsync();
        }
    }

    private async Task LoadPinAsync()
    {
        try
        {
            if (string.IsNullOrEmpty(_preferencesPath) || !Path.IsPathFullyQualified(_preferencesPath))
                throw new InvalidOperationException("The host did not provide a writable preference path. Run with a current host.");
            RunnerPreferences preferences = await Task.Run(() => RunnerPreferences.Load(_preferencesPath), _toolbarLifetime.Token);
            if (_closed) return;
            _topmost = preferences.IsPinned;
            SetTopmost(_topmost);
        }
        catch (OperationCanceledException) when (_closed) { }
        catch (Exception ex) when (IsToolbarFailure(ex))
        {
            if (!_closed) ShowToolbarError("Pin preferences", ex.Message + " (" + _preferencesPath + ")");
        }
        finally
        {
            if (!_closed) _pinButton.IsEnabled = !_isHeadless && _preferencesPath is not null
                && Path.IsPathFullyQualified(_preferencesPath);
        }
    }

    private bool? ReadTopmost()
    {
        try { return AppWindow.Presenter is OverlappedPresenter presenter ? presenter.IsAlwaysOnTop : null; }
        catch (Exception ex) when (IsToolbarFailure(ex))
        {
            CrashLog.Log("Cannot read current Pin state: " + ex.Message);
            return null;
        }
    }

    private void SetTopmost(bool value)
    {
        if (AppWindow.Presenter is not OverlappedPresenter presenter)
            throw new InvalidOperationException("Pin requires a normal overlapped Runner window.");
        bool expected = !_isHeadless && value;
        presenter.IsAlwaysOnTop = expected;
        _pinButton.IsChecked = presenter.IsAlwaysOnTop;
        if (presenter.IsAlwaysOnTop != expected)
            throw new InvalidOperationException("Windows did not apply the requested always-on-top state.");
    }

    private async void PinClickedAsync(object sender, RoutedEventArgs e)
    {
        if (_closed || _isHeadless || _preferencesPath is null) return;
        bool previous = _topmost;
        _pinButton.IsEnabled = false;
        try
        {
            _topmost = _pinButton.IsChecked == true;
            SetTopmost(_topmost);
            var preferences = new RunnerPreferences { IsPinned = _topmost };
            await Task.Run(() => preferences.Save(_preferencesPath), _toolbarLifetime.Token);
            if (!_closed) AppendLog("Pin " + (_topmost ? "on" : "off") + "; saved to " + _preferencesPath);
        }
        catch (OperationCanceledException) when (_closed) { }
        catch (Exception ex) when (IsToolbarFailure(ex))
        {
            if (_closed) return;
            _topmost = previous;
            try { SetTopmost(previous); }
            catch (Exception rollback) when (IsToolbarFailure(rollback))
            {
                _pinButton.IsChecked = ReadTopmost();
                AppendLog("Could not restore previous Pin state: " + rollback.Message);
            }
            ShowToolbarError("Pin was not saved", ex.Message + " (" + _preferencesPath + ")");
        }
        finally { if (!_closed) _pinButton.IsEnabled = true; }
    }

    private async void CodeClickedAsync(object sender, RoutedEventArgs e)
    {
        _codeButton.IsEnabled = false;
        string? path = _toolbarSnippet?.SourcePath;
        try
        {
            await Task.Run(() => CodeEditor.Open(path), _toolbarLifetime.Token);
            if (!_closed) AppendLog("Opened original repro in VS Code: " + path);
        }
        catch (OperationCanceledException) when (_closed) { }
        catch (Exception ex) when (IsToolbarFailure(ex))
        {
            if (!_closed) ShowToolbarError("Open in VS Code", ex.Message);
        }
        finally { if (!_closed) _codeButton.IsEnabled = true; }
    }

    private async void RuntimeClickedAsync(object sender, RoutedEventArgs e)
    {
        if (_toolbarSnippet is null || _requestPath is null || _runtimeDialog is not null) return;
        try
        {
            _runtimeDialog = new RuntimeDialog(_toolbarSnippet, new RunnerControlClient(_requestPath),
                _hwnd, _toolbarLifetime.Token, message => ShowToolbarError("Runtime", message))
            {
                XamlRoot = ((FrameworkElement)Content).XamlRoot,
            };
            UpdateRuntimeAvailability();
            await _runtimeDialog.ShowAsync();
        }
        catch (Exception ex) when (IsToolbarFailure(ex))
        {
            if (!_closed) ShowToolbarError("Runtime", ex.Message);
        }
        finally
        {
            _runtimeDialog?.Dispose();
            _runtimeDialog = null;
            if (!_closed) UpdateRuntimeAvailability();
        }
    }

    private void HostTimerTick(DispatcherQueueTimer sender, object args)
    {
        if (!_closed) UpdateRuntimeAvailability();
    }

    private void UpdateRuntimeAvailability()
    {
        bool available = _toolbarSnippet?.ControlHost?.IsAlive() == true;
        if (_toolbarSnippet is not null)
            _runtimeButton.Label = "API: " + (LoadedSdk.Matches(_toolbarSnippet) ? (_toolbarSnippet.Sdk ?? "unspecified") : "MISMATCH")
                + " | Native: " + RuntimeDialog.DescribeSelection(_toolbarSnippet, compact: true)
                + (available ? string.Empty : " (no watching host)");
        _runtimeButton.IsEnabled = available && !_isHeadless && _runtimeDialog is null;
        ToolTipService.SetToolTip(_runtimeButton, available
            ? LoadedSdk.Context(_toolbarSnippet!) + ". Choose SDK/API and native runtime, save headers, and restart."
            : "Runtime changes require a watching host. Run the repro again without --no-watch.");
    }

    private void ShowToolbarError(string title, string message)
    {
        if (_closed) return;
        _toolbarErrorBar.Title = title;
        _toolbarErrorBar.Message = message;
        _toolbarErrorBar.IsOpen = true;
        AppendLog(title + ": " + message);
    }

    internal static bool IsToolbarFailure(Exception ex) =>
        ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException
            or COMException or NotSupportedException or JsonException or TimeoutException
            or System.ComponentModel.Win32Exception or System.Security.SecurityException;

    private void CloseToolbar()
    {
        _toolbarLifetime.Cancel();
        _runtimeDialog?.Dispose();
        _toolbarLifetime.Dispose();
        _hostTimer?.Stop();
        if (_hostTimer is not null) _hostTimer.Tick -= HostTimerTick;
        _pinButton.Click -= PinClickedAsync;
        _codeButton.Click -= CodeClickedAsync;
        _runtimeButton.Click -= RuntimeClickedAsync;
    }
}
