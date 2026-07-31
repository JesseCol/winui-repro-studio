using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Dispatching;
using ReproStudio.Shared;

namespace ReproStudio_Host.ViewModels;

/// <summary>
/// Drives the editors, the version picker, and the runner. Edits to the XAML or C#
/// are debounced, then written to the request file as a <see cref="Snippet"/>; the
/// separate runner window picks the change up and re-renders. Picking a version
/// provisions that WASDK runtime (download + extract if needed) and relaunches the
/// runner against it.
/// </summary>
public partial class MainViewModel : ObservableObject
{
    private const string DefaultXaml = """
        <StackPanel xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                    Padding="24" Spacing="12">
            <TextBlock Text="Hello from the stage!" FontSize="28" />
            <Button x:Name="HelloButton" Content="Click me" />
        </StackPanel>
        """;

    private const string DefaultCSharp = """
        public static class Repro
        {
            // Setup can ask for the parsed root, the Window, or both.
            // Call Log("...") any time to write to the runner's log panel.
            public static void Setup(FrameworkElement root, Window window)
            {
                window.Title = "My repro";
                Log("Repro loaded.");

                if (root.FindName("HelloButton") is Button button)
                {
                    button.Click += (s, e) =>
                    {
                        button.Content = "Clicked!";
                        Log("Button clicked.");
                    };
                }
            }
        }
        """;

    private readonly RunnerHost _runner;
    private readonly DispatcherQueue _dispatcherQueue;
    private readonly DispatcherQueueTimer _debounceTimer;
    private readonly DispatcherQueueTimer _fileDebounceTimer;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromMinutes(5) };
    private readonly RunnerProvisioner _provisioner;
    private readonly PackagedRunnerLauncher _packagedLauncher;
    private readonly AppLayout _layout;
    private readonly IProgress<ProvisionProgress> _provisionProgress;

    private FileSystemWatcher? _fileWatcher;
    private string? _watchedFilePath;
    private string? _startupFile;
    private bool _loadingFromFile;
    private bool _fileLoadInFlight;
    private bool _fileReloadRequested;
    private string _theme = "Default";
    private string _flowDirection = "LeftToRight";
    private string? _title;

    private bool _switching;

    [ObservableProperty]
    public partial string XamlText { get; set; }

    [ObservableProperty]
    public partial string CSharpText { get; set; }

    [ObservableProperty]
    public partial string StatusText { get; set; }

    [ObservableProperty]
    public partial string? SelectedVersion { get; set; }

    [ObservableProperty]
    public partial WinUiOption? SelectedWinUi { get; set; }

    /// <summary>True while a watched external file is driving the editors.</summary>
    [ObservableProperty]
    public partial bool IsExternalFileMode { get; set; }

    /// <summary>Keep the runner window above other windows.</summary>
    [ObservableProperty]
    public partial bool RunnerTopmost { get; set; }

    /// <summary>Include prerelease packages (preview/experimental) when listing NuGet versions.</summary>
    [ObservableProperty]
    public partial bool AllowPrerelease { get; set; }

    /// <summary>Launch the runner with package identity (a registered package) instead of unpackaged.</summary>
    [ObservableProperty]
    public partial bool LaunchPackaged { get; set; } = true;

    private bool _initializing;
    private bool _rerunRequested;
    private string? _currentExe;

    public MainViewModel(DispatcherQueue dispatcherQueue)
    {
        ArgumentNullException.ThrowIfNull(dispatcherQueue);

        _dispatcherQueue = dispatcherQueue;

        // Prefers a runner-base shipped next to the exe (xcopy bundle) and falls back to
        // the developer one under %LOCALAPPDATA%. Writes always go to the cache root.
        AppLayout layout = AppLayout.Resolve();
        _layout = layout;
        _provisioner = new RunnerProvisioner(_http, layout.CacheRoot);
        _packagedLauncher = new PackagedRunnerLauncher();
        _runner = new RunnerHost(_packagedLauncher);
        _provisionProgress = new Progress<ProvisionProgress>(p => StatusText = p.Message);

        _debounceTimer = dispatcherQueue.CreateTimer();
        _debounceTimer.Interval = TimeSpan.FromMilliseconds(300);
        _debounceTimer.IsRepeating = false;
        _debounceTimer.Tick += (s, e) => SafePush();

        _fileDebounceTimer = dispatcherQueue.CreateTimer();
        _fileDebounceTimer.Interval = TimeSpan.FromMilliseconds(200);
        _fileDebounceTimer.IsRepeating = false;
        _fileDebounceTimer.Tick += (s, e) => _ = SafeLoadFromFileAsync();

        XamlText = DefaultXaml;
        CSharpText = DefaultCSharp;
        StatusText = string.Empty;
    }

    public ObservableCollection<string> Versions { get; } = new();

    public ObservableCollection<WinUiOption> WinUiOptions { get; } = new();

    /// <summary>Writes the first request, loads versions, and launches a runner.</summary>
    public async void Start()
    {
        Push();

        if (!_layout.HasBaseRunner)
        {
            StatusText = _layout.DescribeMissingBaseRunner();
            return;
        }

        _initializing = true;
        StatusText = "Loading Windows App SDK versions...";
        try
        {
            await LoadVersionListsAsync();
        }
#pragma warning disable CA1031 // Surface any network/listing failure to the status bar.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            StatusText = "Could not list versions: " + ex.Message;
            _initializing = false;
            return;
        }

        _initializing = false;
        await ProvisionAndLaunchAsync();

        // If launched with --file, open it now (after a runner is up) so an agent can
        // drive the app without touching the picker.
        if (_startupFile is not null && File.Exists(_startupFile))
        {
            OpenExternalFile(_startupFile);
        }
    }

    /// <summary>Sets a repro file (from <c>--file</c>) to auto-open once started.</summary>
    public void SetStartupFile(string path) => _startupFile = path;

    /// <summary>
    /// Fills the WASDK and WinUI pickers from NuGet, honoring <see cref="AllowPrerelease"/>.
    /// Keeps the current selections when the same versions are still listed and keeps any
    /// local .nupkg WinUI options the user browsed to. Callers must suppress the
    /// selection-changed handlers (set <c>_initializing</c>) so repopulating the lists does
    /// not relaunch the runner on its own.
    /// </summary>
    private async Task LoadVersionListsAsync()
    {
        string? previousVersion = SelectedVersion;
        string previousWinUiToken = CurrentWinUiToken();

        IReadOnlyList<string> versions = await _provisioner.ListWasdkVersionsAsync(AllowPrerelease);

        IReadOnlyList<string> winuiVersions;
        try
        {
            winuiVersions = await _provisioner.ListWinUiVersionsAsync(AllowPrerelease);
        }
#pragma warning disable CA1031 // If the WinUI list fails, keep going with Default only.
        catch
#pragma warning restore CA1031
        {
            winuiVersions = Array.Empty<string>();
        }

        Versions.Clear();
        foreach (string version in versions)
        {
            Versions.Add(version);
        }

        // Rebuild the NuGet-sourced WinUI options but keep any local .nupkg the user added.
        var localOptions = WinUiOptions
            .Where(o => o.Override?.LocalNupkgPath is not null)
            .ToList();
        WinUiOptions.Clear();
        WinUiOptions.Add(new WinUiOption { Display = "Default (matches WASDK)", Override = null });
        foreach (string winuiVersion in winuiVersions)
        {
            WinUiOptions.Add(new WinUiOption
            {
                Display = winuiVersion,
                Override = WinUiOverride.ForVersion(winuiVersion),
            });
        }

        foreach (WinUiOption local in localOptions)
        {
            WinUiOptions.Add(local);
        }

        // Keep the current picks if they still exist; otherwise fall back to newest / Default.
        SelectedVersion = previousVersion is not null && Versions.Contains(previousVersion)
            ? previousVersion
            : versions.FirstOrDefault();
        SelectedWinUi = FindWinUiOptionByToken(previousWinUiToken) ?? WinUiOptions.FirstOrDefault();
    }

    /// <summary>
    /// Re-lists versions after the prerelease toggle flips, preserving the current
    /// selection where possible and only relaunching the runner if that selection had
    /// to change (e.g. a prerelease pick vanished when prerelease was turned off).
    /// </summary>
    private async Task ReloadVersionsAsync()
    {
        string? previousVersion = SelectedVersion;
        string previousWinUiToken = CurrentWinUiToken();

        _initializing = true;
        StatusText = AllowPrerelease
            ? "Reloading versions (including prerelease)..."
            : "Reloading stable versions...";
        try
        {
            await LoadVersionListsAsync();
        }
#pragma warning disable CA1031 // Surface any network/listing failure to the status bar.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            StatusText = "Could not list versions: " + ex.Message;
            _initializing = false;
            return;
        }

        _initializing = false;

        bool selectionChanged =
            !string.Equals(previousVersion, SelectedVersion, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(previousWinUiToken, CurrentWinUiToken(), StringComparison.OrdinalIgnoreCase);
        if (selectionChanged)
        {
            await ProvisionAndLaunchAsync();
        }
        else
        {
            StatusText = string.Empty;
        }
    }

    /// <summary>Finds the option matching a WinUI token ("default", a version, or a local path).</summary>
    private WinUiOption? FindWinUiOptionByToken(string token)
    {
        if (string.Equals(token, "default", StringComparison.OrdinalIgnoreCase))
        {
            return WinUiOptions.FirstOrDefault(o => o.Override is null);
        }

        return WinUiOptions.FirstOrDefault(o =>
            string.Equals(o.Override?.LocalNupkgPath, token, StringComparison.OrdinalIgnoreCase)
            || string.Equals(o.Override?.NuGetVersion, token, StringComparison.OrdinalIgnoreCase));
    }

    public void Shutdown()
    {
        _fileWatcher?.Dispose();
        _runner.Dispose();
    }

    /// <summary>Restarts the runner process for the current version (no re-provision).</summary>
    public async Task RelaunchRunnerAsync()
    {
        if (string.IsNullOrEmpty(_currentExe))
        {
            return;
        }

        (bool launched, string modeNote) = await LaunchRunnerAsync(_currentExe);
        StatusText = launched
            ? $"Runner relaunched{modeNote} (PID {_runner.ProcessId})."
            : "Could not relaunch the runner.";
    }

    /// <summary>
    /// Launches the runner exe in the mode chosen by <see cref="LaunchPackaged"/> (packaged with
    /// identity, or unpackaged). Returns whether the process started and a short note describing
    /// the mode for the status bar.
    /// </summary>
    private async Task<(bool Launched, string ModeNote)> LaunchRunnerAsync(string exe)
    {
        if (LaunchPackaged)
        {
            // Registering the loose-layout package stages a copy, which takes a moment.
            StatusText = "Preparing packaged runner...";
        }

        RunnerHost.LaunchResult result = await _runner.LaunchAsync(exe, GetRunnerBounds(), LaunchPackaged);
        return (result.Launched, result.ModeNote);
    }

    /// <summary>
    /// Clears the provisioned runner cache, then re-provisions and relaunches the
    /// current selection so the change is visible right away. The running runner is
    /// stopped first to release its file locks; downloaded packages are kept.
    /// </summary>
    public async Task ClearCacheAsync()
    {
        _runner.Stop();
        _currentExe = null;

        // Clearing deletes the provisioned runners a packaged registration was staged from, so
        // drop it; the next packaged launch re-registers against the freshly provisioned runner.
        await _runner.UnregisterPackagedAsync();

        StatusText = "Clearing cache...";
        try
        {
            await Task.Run(() => _provisioner.ClearProvisionedRunners());
        }
#pragma warning disable CA1031 // Surface any delete failure to the status bar.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            StatusText = "Could not clear cache: " + ex.Message;
            return;
        }

        StatusText = "Cache cleared. Re-provisioning...";
        await ProvisionAndLaunchAsync();
    }

    /// <summary>
    /// Enters external-file mode: watches a single-file repro (.cs) and refreshes the
    /// runner whenever it is saved. The in-app editors become a read-only mirror.
    /// </summary>
    public void OpenExternalFile(string path)
    {
        ArgumentNullException.ThrowIfNull(path);

        // A picker/await continuation can resume off the UI thread; make sure the
        // load (which touches the editor TextBoxes) always runs on the UI thread.
        if (!_dispatcherQueue.HasThreadAccess)
        {
            _dispatcherQueue.TryEnqueue(() => OpenExternalFile(path));
            return;
        }

        _watchedFilePath = path;
        IsExternalFileMode = true;

        _fileWatcher?.Dispose();
        string? dir = Path.GetDirectoryName(path);
        string file = Path.GetFileName(path);
        if (!string.IsNullOrEmpty(dir))
        {
            _fileWatcher = new FileSystemWatcher(dir, file)
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
                EnableRaisingEvents = true,
            };
            _fileWatcher.Changed += OnWatchedFileChanged;
            _fileWatcher.Created += OnWatchedFileChanged;
            _fileWatcher.Renamed += OnWatchedFileChanged;
        }

        _ = LoadFromFileAsync();
    }

    private void OnWatchedFileChanged(object sender, FileSystemEventArgs e)
    {
        // Watcher events arrive on a threadpool thread; debounce on the UI thread.
        _dispatcherQueue.TryEnqueue(() =>
        {
            _fileDebounceTimer.Stop();
            _fileDebounceTimer.Start();
        });
    }

    /// <summary>
    /// Reads the watched file, parses it, mirrors it into the editors, and refreshes
    /// the runner. Serialized so overlapping watcher events can't run two loads at
    /// once (which could re-enter the editor mirror); a request during a load queues
    /// one more pass.
    /// </summary>
    private async Task LoadFromFileAsync()
    {
        if (_fileLoadInFlight)
        {
            _fileReloadRequested = true;
            return;
        }

        _fileLoadInFlight = true;
        try
        {
            do
            {
                _fileReloadRequested = false;
                await LoadFromFileOnceAsync();
            }
            while (_fileReloadRequested);
        }
        finally
        {
            _fileLoadInFlight = false;
        }
    }

    private async Task LoadFromFileOnceAsync()
    {
        string? path = _watchedFilePath;
        if (string.IsNullOrEmpty(path))
        {
            return;
        }

        string name = Path.GetFileName(path);

        // Read synchronously on the UI thread. The file is tiny, and hopping to a
        // background thread (Task.Run) risks the async continuation resuming off the
        // UI thread, where touching the editor TextBoxes hard-crashes WinUI.
        string? text = TryReadAllText(path);
        if (text is null)
        {
            StatusText = $"Could not read {name} (locked?).";
            return;
        }

        try
        {
            ParsedSnippetFile parsed = SnippetFileParser.Parse(text);

            _loadingFromFile = true;
            XamlText = parsed.Xaml;
            CSharpText = parsed.CSharp;
            _loadingFromFile = false;

            _theme = parsed.Theme;
            _flowDirection = parsed.FlowDirection;
            _title = parsed.Title;

            // Write the freshest content first, so a runner we (re)launch reads it.
            Push();

            string desiredVersion = string.IsNullOrEmpty(parsed.WasdkVersion)
                ? SelectedVersion ?? string.Empty
                : ResolveVersionToken(parsed.WasdkVersion!);
            string desiredToken = parsed.WinUiToken ?? "default";

            bool launchChanged =
                !string.Equals(desiredVersion, SelectedVersion, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(desiredToken, CurrentWinUiToken(), StringComparison.OrdinalIgnoreCase);

            string loadedStatus = string.IsNullOrWhiteSpace(parsed.Xaml)
                ? $"No `string Xaml` found in {name}."
                : string.IsNullOrEmpty(_title) ? $"Loaded {name}." : $"Loaded \"{_title}\".";

            if (launchChanged && !string.IsNullOrEmpty(desiredVersion))
            {
                ApplyLaunchSelection(desiredVersion, parsed.WinUiToken);
                await ProvisionAndLaunchAsync();

                // ProvisionAndLaunchAsync owns the status; re-assert a missing-XAML note.
                if (string.IsNullOrWhiteSpace(parsed.Xaml))
                {
                    StatusText = loadedStatus;
                }
            }
            else
            {
                StatusText = loadedStatus;
            }
        }
#pragma warning disable CA1031 // Surface any load failure to the status bar instead of crashing the host.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            _loadingFromFile = false;
            HostLog.Log("LoadFromFile failed: " + ex);
            StatusText = $"Failed to load {name}: {ex.Message}";
        }
    }

    /// <summary>
    /// Resolves a possibly-partial WASDK token against the versions we listed.
    /// See <see cref="VersionResolver"/>.
    /// </summary>
    private string ResolveVersionToken(string token) => VersionResolver.Resolve(token, Versions);

    private void ApplyLaunchSelection(string version, string? winuiToken)
    {
        // Suppress the selection-changed handlers; we launch explicitly below.
        _initializing = true;
        try
        {
            if (!Versions.Contains(version))
            {
                Versions.Insert(0, version);
            }

            SelectedVersion = version;
            SelectedWinUi = ResolveWinUiOption(winuiToken);
        }
        finally
        {
            _initializing = false;
        }
    }

    private WinUiOption ResolveWinUiOption(string? token)
    {
        bool isDefault = string.IsNullOrEmpty(token)
            || string.Equals(token, "default", StringComparison.OrdinalIgnoreCase);
        if (isDefault)
        {
            return EnsureDefaultWinUiOption();
        }

        if (token!.EndsWith(".nupkg", StringComparison.OrdinalIgnoreCase))
        {
            if (!File.Exists(token))
            {
                StatusText = $"WinUI package not found: {token}. Using default.";
                return EnsureDefaultWinUiOption();
            }

            WinUiOverride localOverride = WinUiOverride.ForLocalPackage(token);
            WinUiOption? existingLocal = WinUiOptions.FirstOrDefault(
                o => o.Override?.CacheKey == localOverride.CacheKey);
            if (existingLocal is not null)
            {
                return existingLocal;
            }

            var localOption = new WinUiOption
            {
                Display = Path.GetFileName(token) + " (local)",
                Override = localOverride,
            };
            WinUiOptions.Add(localOption);
            return localOption;
        }

        WinUiOption? match = WinUiOptions.FirstOrDefault(
            o => string.Equals(o.Display, token, StringComparison.OrdinalIgnoreCase));
        if (match is not null)
        {
            return match;
        }

        var versionOption = new WinUiOption
        {
            Display = token,
            Override = WinUiOverride.ForVersion(token),
        };
        WinUiOptions.Add(versionOption);
        return versionOption;
    }

    private WinUiOption EnsureDefaultWinUiOption()
    {
        WinUiOption? existing = WinUiOptions.FirstOrDefault(o => o.Override is null);
        if (existing is not null)
        {
            return existing;
        }

        var option = new WinUiOption { Display = "Default (matches WASDK)", Override = null };
        WinUiOptions.Insert(0, option);
        return option;
    }

    private string CurrentWinUiToken()
    {
        WinUiOverride? ov = SelectedWinUi?.Override;
        if (ov is null)
        {
            return "default";
        }

        return ov.LocalNupkgPath ?? ov.NuGetVersion ?? "default";
    }

    private static string? TryReadAllText(string path)
    {
        for (int attempt = 1; attempt <= 5; attempt++)
        {
            try
            {
                return File.ReadAllText(path);
            }
            catch (IOException)
            {
                // Editor may still be mid-save; retry shortly.
                Thread.Sleep(60);
            }
            catch (UnauthorizedAccessException)
            {
                Thread.Sleep(60);
            }
        }

        return null;
    }

    /// <summary>
    /// Where to place the runner window: flush to the right of the host window,
    /// same height, 600px wide. Null if the host window isn't available yet.
    /// </summary>
    private static (int X, int Y, int Width, int Height)? GetRunnerBounds()
    {
        var appWindow = MainWindow.Instance?.AppWindow;
        if (appWindow is null)
        {
            return null;
        }

        var position = appWindow.Position;
        var size = appWindow.Size;
        return (position.X + size.Width, position.Y, 600, size.Height);
    }

    /// <summary>Adds a local WinUI .nupkg to the dropdown and selects it.</summary>
    public void AddLocalWinUiPackage(string nupkgPath)
    {
        ArgumentNullException.ThrowIfNull(nupkgPath);

        var option = new WinUiOption
        {
            Display = Path.GetFileName(nupkgPath) + " (local)",
            Override = WinUiOverride.ForLocalPackage(nupkgPath),
        };
        WinUiOptions.Add(option);
        SelectedWinUi = option;
    }

    partial void OnSelectedVersionChanged(string? value)
    {
        if (!_initializing)
        {
            _ = ProvisionAndLaunchAsync();
        }
    }

    partial void OnSelectedWinUiChanged(WinUiOption? value)
    {
        if (!_initializing)
        {
            _ = ProvisionAndLaunchAsync();
        }
    }

    partial void OnXamlTextChanged(string value)
    {
        if (!_loadingFromFile)
        {
            SchedulePush();
        }
    }

    partial void OnCSharpTextChanged(string value)
    {
        if (!_loadingFromFile)
        {
            SchedulePush();
        }
    }

    partial void OnRunnerTopmostChanged(bool value) => Push();

    partial void OnLaunchPackagedChanged(bool value)
    {
        // Packaged vs unpackaged is decided at launch, so relaunch to apply the change.
        if (!_initializing && !string.IsNullOrEmpty(_currentExe))
        {
            _ = RelaunchRunnerAsync();
        }
    }

    partial void OnAllowPrereleaseChanged(bool value)
    {
        if (!_initializing)
        {
            _ = ReloadVersionsAsync();
        }
    }

    private async Task ProvisionAndLaunchAsync()
    {
        if (_switching)
        {
            // A switch is already running; ask it to re-run with the latest selection.
            _rerunRequested = true;
            return;
        }

        _switching = true;
        try
        {
            do
            {
                _rerunRequested = false;

                string? version = SelectedVersion;
                if (string.IsNullOrEmpty(version))
                {
                    break;
                }

                WinUiOption? winuiOption = SelectedWinUi;
                WinUiOverride? winui = winuiOption?.Override;

                try
                {
                    StatusText = $"Preparing Windows App SDK {version}...";
                    string exe = await Task.Run(() =>
                        _provisioner.EnsureRunnerAsync(version, _layout.BaseRunnerDir, winui, _provisionProgress));

                    _currentExe = exe;
                    string winuiSuffix = winui is null ? string.Empty : $" + WinUI {winuiOption!.Display}";
                    (bool launched, string modeNote) = await LaunchRunnerAsync(exe);
                    StatusText = launched
                        ? $"Running on Windows App SDK {version}{winuiSuffix}{modeNote} (PID {_runner.ProcessId})."
                        : $"Could not launch the runner for {version}.";
                }
#pragma warning disable CA1031 // Surface any provisioning/launch failure to the status bar.
                catch (Exception ex)
#pragma warning restore CA1031
                {
                    StatusText = $"Failed to prepare {version}: {ex.Message}";
                }
            }
            while (_rerunRequested);
        }
        finally
        {
            _switching = false;
        }
    }

    private void SchedulePush()
    {
        _debounceTimer.Stop();
        _debounceTimer.Start();
    }

    private void Push() => _runner.WriteRequest(BuildSnippet());

    /// <summary>
    /// Timer-tick entry points must never let an exception escape: an unhandled
    /// exception in a DispatcherQueue callback fail-fasts the whole process (it
    /// bypasses XAML's UnhandledException). So these wrappers swallow-and-log.
    /// </summary>
    private void SafePush()
    {
        try
        {
            Push();
        }
#pragma warning disable CA1031 // A timer callback must never crash the host.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            HostLog.Log("SafePush failed: " + ex);
        }
    }

    private async Task SafeLoadFromFileAsync()
    {
        try
        {
            await LoadFromFileAsync();
        }
#pragma warning disable CA1031 // A timer callback must never crash the host.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            HostLog.Log("SafeLoadFromFile failed: " + ex);
        }
    }

    private Snippet BuildSnippet() => new()
    {
        Title = _title,
        Xaml = XamlText,
        CSharp = CSharpText,
        Theme = _theme,
        FlowDirection = _flowDirection,
        Topmost = RunnerTopmost,
        WasdkVersion = SelectedVersion,
    };
}
