using ReproStudio.Shared;

namespace ReproStudio_Cli;

/// <summary>
/// One repro file, driven end to end: parse it, get a runner for the version it asks
/// for, launch it, then watch the file and push every save.
/// <para>
/// Some header keys are live (theme, XAML, C#) and only need a new request written.
/// Others are launch-time (WASDK version, WinUI override, package identity, DPI) and
/// need a different runner process. <see cref="LaunchPlan"/> is the line between them:
/// when it changes, we relaunch; when it does not, we just push.
/// </para>
/// </summary>
internal sealed partial class ReproSession : IDisposable
{
    /// <summary>How long to wait after a file change, so one save is one reload.</summary>
    private static readonly TimeSpan SaveDebounce = TimeSpan.FromMilliseconds(150);

    /// <summary>
    /// Longer debounce for the payload folder. Copying a runtime DLL in takes a moment
    /// and raises events throughout, so a save-sized delay would fire mid-copy.
    /// </summary>
    private static readonly TimeSpan PayloadDebounce = TimeSpan.FromSeconds(2);

    /// <summary>How often to notice that the runner died on its own.</summary>
    private static readonly TimeSpan HealthInterval = TimeSpan.FromSeconds(1);

    private static readonly TimeSpan KeyboardPollInterval = TimeSpan.FromMilliseconds(100);

    private static readonly TimeSpan CaptureTimeout = TimeSpan.FromSeconds(60);

    private readonly CliOptions _options;
    private readonly AppLayout _layout;
    private readonly string _filePath;
    private readonly string? _screenshotPath;

    private readonly RunnerProvisioner _provisioner;
    private readonly RunnerHost _host;
    private readonly RunnerControlHost _controlHost = new();
    private bool _useRuntimeHeaders;
    private string? _pendingHeaderReloadText;

    /// <summary>Serialises reloads against each other and against the health check.</summary>
    private readonly SemaphoreSlim _gate = new(1, 1);

    private FileSystemWatcher? _watcher;
    private FileSystemWatcher? _payloadWatcher;
    private System.Threading.Timer? _debounce;
    private CancellationToken _ct;

    private readonly object _versionsLock = new();

    /// <summary>Shared by version resolution and the console shortcut; failed lookups can retry.</summary>
    private Task<IReadOnlyList<string>>? _versionsTask;

    private volatile bool _canReadKeys;

    /// <summary>
    /// The last version we told the user a partial token resolved to. Watch mode
    /// re-resolves on every save, and repeating the same line each time is noise.
    /// </summary>
    private string? _lastResolvedLogged;

    /// <summary>What the running runner was launched with, or null if none is running.</summary>
    private LaunchPlan? _running;
    private RunnerPairInfo? _runningPair;

    /// <summary>Size of runner.log when we launched, so a crash report shows only new lines.</summary>
    private long _logOffset;
    private Guid _requestId;
    private string _requestSourceText = string.Empty;
    private Guid _reportedCapture;
    private Guid _timedOutCapture;
    private long _captureDeadline;

    public ReproSession(CliOptions options, AppLayout layout)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(layout);

        _options = options;
        _layout = layout;
        _filePath = options.File is { } file
            ? Path.GetFullPath(file)
            : Path.Combine(AppContext.BaseDirectory, "samples", "hello.cs");
        _screenshotPath = options.Screenshot is { } screenshot
            ? Path.GetFullPath(screenshot)
            : options.Headless ? Path.GetFullPath("ReproStudio.png") : null;

        // A nuget.config next to the repro file is honoured, so a repro can travel with
        // the feed it needs (an internal WinUI feed, say).
        _provisioner = new RunnerProvisioner(layout.CacheRoot, Path.GetDirectoryName(_filePath));
        _host = new RunnerHost(new PackagedRunnerLauncher());
    }

    /// <summary>
    /// Everything that forces a new runner process when it changes. <see cref="Version"/> is
    /// null when a WinUI package is standing on its own: the package's declared dependencies
    /// pick the stack, so there is no Windows App SDK version to name.
    /// </summary>
    private readonly record struct LaunchPlan(
        string? Version,
        string Sdk,
        string WinUiKey,
        string PayloadKey,
        string ProcessLaunchKey,
        bool Packaged,
        int Dpi)
    {
        public string Describe(bool includePackageIdentity = true) => "API " + Sdk + "; native " + (Version ?? "winui-only")
            + (WinUiKey.Length == 0 ? string.Empty : " + winui " + WinUiKey)
            + (PayloadKey.Length == 0 ? string.Empty : " + payload " + PayloadKey)
            + (ProcessLaunchKey.Length == 0 ? string.Empty : " + process launch hook")
            + (includePackageIdentity && Packaged ? " (packaged)" : string.Empty);
    }

    /// <summary>Runs the repro. Returns a process exit code.</summary>
    public async Task<int> RunAsync(CancellationToken ct)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _ct = cancellation.Token;

        if (!File.Exists(_filePath))
        {
            Log.Error(_options.File is null
                ? "Bundled default sample is missing: " + _filePath
                    + ". Rebuild with dotnet build or re-extract the bundle, or pass a .cs file."
                : "No such file: " + _filePath);
            return 1;
        }

        if (string.Equals(_filePath, _screenshotPath, StringComparison.OrdinalIgnoreCase))
        {
            Log.Error("The screenshot path must not overwrite the repro file.");
            return 1;
        }

        if (!_layout.HasBaseRunner)
        {
            Log.Error(_layout.DescribeMissingBaseRunner());
            return 1;
        }

        Log.Field("file", _filePath);
        if (_options.File is null)
        {
            Log.Detail("Starting the hello sample. Edit the file above and save to change the preview.");
        }

        Log.Field("cache", _layout.CacheRoot);
        Log.Field("runner", _layout.BaseRunnerDir, _layout.IsPortable ? "(portable)" : "(dev)");
        Log.Field("runner log", RunnerLogPath);
        if (_screenshotPath is not null && !_options.ProvisionOnly)
        {
            Log.Field("screenshot", _screenshotPath);
            try
            {
                string directory = Path.GetDirectoryName(_screenshotPath)
                    ?? throw new ArgumentException("The screenshot path needs a parent directory.");
                Directory.CreateDirectory(directory);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log.Error("Could not prepare the screenshot folder: " + ex.Message);
                return 1;
            }

            if (_options.Headless)
            {
                Log.Detail("Headless: the Runner window stays cloaked. Capture fallbacks are reported.");
            }
        }

        if (_options.ClearCache)
        {
            Log.Step("clear cache");
            await _host.UnregisterPackagedAsync().ConfigureAwait(false);
            _provisioner.ClearProvisionedRunners();
            Log.Ok("provisioned runners deleted (downloads kept)");
        }

        if (!await ApplyAsync(firstRun: true).ConfigureAwait(false))
        {
            return 1;
        }

        if (!_options.Watch)
        {
            if (!_options.ProvisionOnly && _screenshotPath is not null)
            {
                bool captured = await WaitForCaptureAsync().ConfigureAwait(false);
                if (_options.Headless)
                {
                    _host.Stop();
                    Log.Detail("Headless runner stopped.");
                }
                return captured ? 0 : 1;
            }

            Log.Blank();
            Log.Ok(_options.ProvisionOnly
                ? "Runner ready. Nothing launched."
                : "Runner left running. Re-run to pick up edits.");
            PrintEditPath();
            return 0;
        }

        Log.Step("watching");
        _canReadKeys = !Console.IsInputRedirected;
        StartWatching();
        PrintWatchGuidance();

        await WatchUntilCancelledAsync(cancellation).ConfigureAwait(false);

        Log.Blank();
        Log.Ok("Stopped.");
        return 0;
    }

    private async Task<bool> WaitForCaptureAsync()
    {
        long deadline = Environment.TickCount64 + (long)CaptureTimeout.TotalMilliseconds;
        while (Environment.TickCount64 < deadline)
        {
            _ct.ThrowIfCancellationRequested();
            if (ReadCurrentCapture() is { } result)
            {
                return ReportCapture(result);
            }

            if (_host.ProcessId is null)
            {
                Log.Error("The runner exited before completing the screenshot.");
                ReportRunnerLog();
                return false;
            }

            await Task.Delay(100, _ct).ConfigureAwait(false);
        }

        Log.Error("The runner did not complete rendering and capture within 60 seconds.");
        Log.Detail("Expected result: " + _host.ResultPath);
        ReportRunnerLog();
        return false;
    }

    private RunnerResult? ReadCurrentCapture()
    {
        RunnerResult? result = _host.ReadResult();
        return result?.RequestId == _requestId ? result : null;
    }

    private bool ReportCapture(RunnerResult result)
    {
        _reportedCapture = result.RequestId;
        bool success = result.RenderSucceeded;
        if (!success)
        {
            Log.Error("Render failed: " + (result.RenderError ?? "The runner supplied no error detail."));
        }

        if (result.CaptureWarning is { Length: > 0 } warning)
        {
            Log.Warn("Screenshot fallback: " + warning);
        }

        if (result.CaptureError is { Length: > 0 } error)
        {
            Log.Error("Screenshot failed: " + error);
            return false;
        }

        if (!string.Equals(result.ScreenshotPath, _screenshotPath, StringComparison.OrdinalIgnoreCase)
            || !File.Exists(_screenshotPath)
            || result.CaptureMethod is not ("Windows.Graphics.Capture" or "RenderTargetBitmap"))
        {
            Log.Error("The runner did not produce the requested screenshot with a recognized capture method.");
            return false;
        }

        Log.Ok("screenshot: " + _screenshotPath + " (" + result.CaptureMethod + ")");
        if (result.CaptureMethod == "RenderTargetBitmap")
        {
            Log.Detail("XAML snapshot only: native frames, popup windows and other non-XAML content may be missing.");
        }

        return success;
    }

    public void Dispose()
    {
        _watcher?.Dispose();
        _payloadWatcher?.Dispose();
        _debounce?.Dispose();
        _gate.Dispose();

        // Only a visible no-watch runner is meant to outlive us.
        if (_options.Watch || _options.Headless)
        {
            _host.Dispose();
        }

        _provisioner.Dispose();
    }

    /// <summary>
    /// Reads the file and makes the runner match it: relaunching when a launch-time key
    /// changed, otherwise just pushing a new request. Returns false only on a hard failure.
    /// </summary>
    private async Task<bool> ApplyAsync(bool firstRun)
    {
        string? text = TryReadAllText(_filePath);
        if (text is null)
        {
            Log.Error("Could not read the file (it may be locked by the editor). Save again to retry.");
            return false;
        }

        ParsedSnippetFile parsed = SnippetFileParser.Parse(text);
        bool isHeaderNotification = _pendingHeaderReloadText == text;
        _pendingHeaderReloadText = null;

        if (!parsed.HasXaml)
        {
            Log.Warn("No 'string Xaml = ...' literal found, so there is nothing to render.");
            Log.Detail("Keep the class wrapper and Xaml literal from samples\\hello.cs.");
        }

        LaunchPlan plan;
        WinUiOverride? winui;
        RunnerPayload? payload;
        try
        {
            winui = ResolveWinUi(parsed.WinUiToken);
            payload = ResolvePayload(parsed.PayloadDir);
            string? version = await ResolveVersionAsync(parsed.WasdkVersion, winui).ConfigureAwait(false);
            plan = new LaunchPlan(
                version,
                await ResolveSdkAsync((_useRuntimeHeaders ? null : _options.Sdk) ?? parsed.Sdk, _ct).ConfigureAwait(false),
                winui?.CacheKey ?? string.Empty,
                payload?.Fingerprint ?? string.Empty,
                parsed.ProcessLaunchKey,
                _options.Packaged ?? parsed.Packaged ?? false,
                parsed.Dpi ?? 100);
        }
        catch (Exception ex) when (ex is IOException or HttpRequestException or InvalidOperationException or TaskCanceledException or ArgumentException)
        {
            Log.Error(ex.Message);
            return false;
        }

        Snippet snippet = BuildSnippet(parsed, plan, winui);

        if (_running == plan)
        {
            // The header commit's watcher event must not render twice after a UI restart.
            if (isHeaderNotification) return true;
            WriteRenderRequest(snippet, text);
            _captureDeadline = Environment.TickCount64 + (long)CaptureTimeout.TotalMilliseconds;
            Log.Event("pushed" + (parsed.Title is { Length: > 0 } t ? "  " + t : string.Empty));
            return true;
        }

        if (!firstRun)
        {
            Log.Event("relaunching: " + plan.Describe());
        }

        return await ProvisionAndLaunchAsync(plan, winui, payload, snippet, text, firstRun).ConfigureAwait(false);
    }

    private async Task<bool> ProvisionAndLaunchAsync(LaunchPlan plan, WinUiOverride? winui, RunnerPayload? payload, Snippet snippet, string sourceText, bool firstRun)
    {
        if (firstRun)
        {
            Log.Step("provision");
            Log.Field("API choice", plan.Sdk);
            if (plan.Version is not null)
            {
                Log.Field("wasdk", plan.Version);
            }

            if (winui is not null)
            {
                Log.Field("winui", winui.LocalNupkgPath ?? winui.NuGetVersion ?? "default");
                if (plan.Version is null)
                {
                    Log.Detail("No Windows App SDK version asked for, so this package's own dependencies pick the stack.");
                }
            }

            if (payload is not null)
            {
                Log.Field("payload", $"{payload.RelativePaths.Count} file(s) from {payload.Directory}");
                foreach (string relative in payload.RelativePaths)
                {
                    Log.Detail(relative);
                }
            }

            Log.Field("packaged", plan.Packaged ? "yes" : "no");
        }

        string exe;
        try
        {
            var progress = new Progress<ProvisionProgress>(p => Log.Detail(p.Message));
            exe = await _provisioner
                .EnsureRunnerAsync(plan.Version, _layout.BaseRunnerDir, winui, payload, progress, _ct, plan.Sdk)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            Log.Error("Could not prepare a runner for " + plan.Describe() + ": " + ex.Message);
            if (ex.Message.Contains("not found on any package source", StringComparison.Ordinal))
            {
                Log.Detail("Run with --list to see the versions NuGet actually has.");
            }
            return false;
        }

        if (firstRun)
        {
            Log.Ok("ready: " + exe);
        }

        if (_options.ProvisionOnly)
        {
            return true;
        }

        return await LaunchPreparedAsync(plan, exe, snippet, sourceText, firstRun).ConfigureAwait(false);
    }

    private void WriteRenderRequest(Snippet snippet, string sourceText)
    {
        _requestId = _host.WriteRequest(snippet);
        _requestSourceText = sourceText;
    }

    private async Task<bool> LaunchPreparedAsync(LaunchPlan plan, string exe, Snippet snippet, string sourceText, bool firstRun)
    {
        snippet.Pair = RunnerPairManifest.Read(Path.GetDirectoryName(exe)!)?.Pair
            ?? throw new InvalidOperationException("Provisioned Runner is missing verified SDK/runtime metadata.");
        Log.Field("API/SDK", snippet.Pair.ApiLabel);
        Log.Field("native", snippet.Pair.NativeLabel);
        Log.Field("managed", "Microsoft.WinUI " + snippet.Pair.WinUiFileVersion, snippet.Pair.RuntimeIdentifier);
        if (firstRun)
        {
            Log.Step("launch");
        }

        _logOffset = CurrentRunnerLogLength();

        // Do not let the old runtime render a request claiming the new runtime.
        // Provisioning and all fallible source preflight happened before this point.
        _host.Stop();
        WriteRenderRequest(snippet, sourceText);
        RunnerHost.LaunchResult result = await _host
            .LaunchAsync(
                exe,
                bounds: null,
                plan.Packaged,
                runProcessLaunch: plan.ProcessLaunchKey.Length > 0,
                headless: _options.Headless,
                screenshotPath: _screenshotPath)
            .ConfigureAwait(false);
        if (!result.Launched)
        {
            Log.Error("The runner did not start." + result.ModeNote);
            ReportRunnerLog();
            _running = null;
            return false;
        }

        // A packaged launch can silently fall back to unpackaged, which changes what is
        // actually under test. Say so loudly rather than letting it pass as success.
        if (plan.Packaged && !result.IsPackaged)
        {
            Log.Warn("Requested packaged, but" + result.ModeNote);
            Log.Detail("Run --doctor to check Developer Mode.");
        }

        _running = plan;
        _runningPair = snippet.Pair;
        _captureDeadline = Environment.TickCount64 + (long)CaptureTimeout.TotalMilliseconds;

        string mode = result.ModeNote.Trim();
        if (firstRun)
        {
            Log.Ok("running" + (mode.Length == 0 ? string.Empty : " " + mode) + ", pid " + _host.ProcessId);
        }
        else
        {
            Log.Event("running " + plan.Describe(includePackageIdentity: false)
                + (mode.Length == 0 ? string.Empty : " " + mode));
        }

        return true;
    }

    private Snippet BuildSnippet(ParsedSnippetFile parsed, LaunchPlan plan, WinUiOverride? winui) => new()
    {
        SourcePath = _filePath,
        PreferencesPath = RunnerPreferences.GetPath(_layout.CacheRoot),
        ControlHost = _options.Watch ? _controlHost : null,
        Title = parsed.Title,
        WasdkVersion = plan.Version,
        WinUiToken = winui?.LocalNupkgPath ?? winui?.NuGetVersion,
        Sdk = plan.Sdk,
        Pair = _running == plan ? _runningPair : null,
        Dpi = plan.Dpi,
        Theme = parsed.Theme,
        FlowDirection = parsed.FlowDirection,
        Background = parsed.Background,
        Xaml = parsed.Xaml,
        CSharp = parsed.CSharp,
    };

    private async Task<string> ResolveSdkAsync(string? token, CancellationToken ct)
    {
        string sdk = SdkSelection.Normalize(token);
        string resolved = await SdkSelection.ResolveAsync(sdk, GetVersionsAsync, ct).ConfigureAwait(false);
        if (resolved != sdk) Log.Detail("API/SDK " + sdk + " resolved to " + resolved);
        return resolved;
    }

    /// <summary>
    /// Turns a version token into a real version. A fully written version is used as-is so
    /// a pinned repro file works with no network at all; anything shorter (or missing) needs
    /// the list from NuGet.
    ///
    /// Returns null when a WinUI package was given and no Windows App SDK version was asked
    /// for. That package's own dependencies then decide the whole stack, which is what you
    /// want for a nupkg out of the WinUI repo's 'build.cmd /version'.
    /// </summary>
    private async Task<string?> ResolveVersionAsync(string? headerToken, WinUiOverride? winui)
    {
        string? token = (_useRuntimeHeaders ? null : _options.Wasdk) ?? headerToken;

        if (token is not { Length: > 0 } && winui is not null)
        {
            return null;
        }

        if (token is { Length: > 0 } && token.Count(c => c == '.') >= 2)
        {
            return token;
        }

        IReadOnlyList<string>? versions = await TryListVersionsAsync().ConfigureAwait(false);

        if (versions is null || versions.Count == 0)
        {
            if (token is { Length: > 0 })
            {
                Log.Warn("Could not reach NuGet, so using '" + token + "' as written.");
                return token;
            }

            throw new InvalidOperationException(
                "No Windows App SDK version given and NuGet could not be reached. "
                + "Add a '// wasdk: <version>' header or pass --wasdk.");
        }

        if (token is not { Length: > 0 })
        {
            Log.Detail("No version asked for, using the newest: " + versions[0]);
            return versions[0];
        }

        string resolved = VersionResolver.Resolve(token, versions);
        if (!string.Equals(resolved, token, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(resolved, _lastResolvedLogged, StringComparison.OrdinalIgnoreCase))
        {
            Log.Detail(token + " resolved to " + resolved);
            _lastResolvedLogged = resolved;
        }

        return resolved;
    }

    private Task<IReadOnlyList<string>> GetVersionsAsync()
    {
        lock (_versionsLock)
        {
            if (_versionsTask is null || _versionsTask.IsFaulted || _versionsTask.IsCanceled)
            {
                _versionsTask = _provisioner.ListWasdkVersionsAsync(_options.Prerelease, _ct);
            }

            return _versionsTask;
        }
    }

    private async Task<IReadOnlyList<string>?> TryListVersionsAsync()
    {
        try
        {
            return await GetVersionsAsync().ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (WasdkVersionList.IsExpectedFailure(ex))
        {
            return null;
        }
    }

    /// <summary>
    /// Turns a <c>// winui:</c> token into an override. A <c>.nupkg</c> path is resolved
    /// relative to the repro file, so a repro can sit next to the private build it tests.
    /// </summary>
    private WinUiOverride? ResolveWinUi(string? headerToken)
    {
        string? token = (_useRuntimeHeaders ? null : _options.WinUi) ?? headerToken;
        if (token is not { Length: > 0 } || token.Equals("default", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        if (!token.EndsWith(".nupkg", StringComparison.OrdinalIgnoreCase))
        {
            return WinUiOverride.ForVersion(token);
        }

        string path = Path.IsPathRooted(token)
            ? token
            : Path.GetFullPath(Path.Combine(Path.GetDirectoryName(_filePath)!, token));

        if (!File.Exists(path))
        {
            throw new FileNotFoundException("WinUI package not found: " + path);
        }

        return WinUiOverride.ForLocalPackage(path);
    }

    /// <summary>
    /// Works out which drop folder to use, in order: <c>--payload</c>, then the file's
    /// <c>// payload:</c> header, then a <c>payload\</c> folder next to the exe.
    /// <para>
    /// An explicitly named folder that is missing is an error - it almost always means a
    /// typo, and silently running stock bits while you believe you are testing a private
    /// build is the worst possible failure. The default folder is allowed to be missing.
    /// </para>
    /// </summary>
    private RunnerPayload? ResolvePayload(string? headerToken)
    {
        string? token = _options.Payload ?? headerToken;
        if (token is not { Length: > 0 })
        {
            return RunnerPayload.FromDirectory(_layout.DefaultPayloadDir);
        }

        if (token.Equals("none", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        string path = Path.IsPathRooted(token)
            ? token
            : Path.GetFullPath(Path.Combine(Path.GetDirectoryName(_filePath)!, token));

        if (!Directory.Exists(path))
        {
            throw new DirectoryNotFoundException("Payload folder not found: " + path);
        }

        return RunnerPayload.FromDirectory(path);
    }

    private void StartWatching()
    {
        _debounce = new System.Threading.Timer(_ => _ = ReloadAsync(), null, Timeout.Infinite, Timeout.Infinite);

        _watcher = new FileSystemWatcher(Path.GetDirectoryName(_filePath)!, Path.GetFileName(_filePath))
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName,
        };

        // Editors save in different ways (write in place, or write a temp file and rename),
        // so watch every event that can mean "the file now has new content".
        _watcher.Changed += (_, _) => Schedule();
        _watcher.Created += (_, _) => Schedule();
        _watcher.Renamed += (_, _) => Schedule();
        _watcher.EnableRaisingEvents = true;

        WatchPayloadFolder();
    }

    /// <summary>
    /// Watches the drop folder too, so rebuilding a runtime DLL and copying it in
    /// relaunches the repro without touching the .cs file.
    /// </summary>
    private void WatchPayloadFolder()
    {
        string? explicitDir = _options.Payload;
        if (explicitDir is { Length: > 0 } && explicitDir.Equals("none", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        string dir = explicitDir is { Length: > 0 }
            ? Path.GetFullPath(explicitDir)
            : _layout.DefaultPayloadDir;

        if (!Directory.Exists(dir))
        {
            return;
        }

        _payloadWatcher = new FileSystemWatcher(dir)
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName,
        };

        _payloadWatcher.Changed += (_, _) => SchedulePayload();
        _payloadWatcher.Created += (_, _) => SchedulePayload();
        _payloadWatcher.Deleted += (_, _) => SchedulePayload();
        _payloadWatcher.Renamed += (_, _) => SchedulePayload();
        _payloadWatcher.EnableRaisingEvents = true;

        Log.Detail("watching " + dir);
    }

    /// <summary>Restarts the debounce, so a burst of events becomes one reload.</summary>
    private void Schedule() => _debounce?.Change(SaveDebounce, Timeout.InfiniteTimeSpan);

    /// <summary>As <see cref="Schedule"/>, but waits long enough for a big file copy to finish.</summary>
    private void SchedulePayload() => _debounce?.Change(PayloadDebounce, Timeout.InfiniteTimeSpan);

    private async Task ReloadAsync()
    {
        if (_ct.IsCancellationRequested)
        {
            return;
        }

        await _gate.WaitAsync(_ct).ConfigureAwait(false);
        try
        {
            await ApplyAsync(firstRun: false).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Shutting down.
        }
#pragma warning disable CA1031 // A bad edit must not take down the watch loop.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            Log.Error(ex.Message);
        }
        finally
        {
            try
            {
                if (!_ct.IsCancellationRequested)
                {
                    PrintWatchGuidance();
                }
            }
            finally
            {
                _gate.Release();
            }
        }
    }

    private void PrintEditPath() => Log.Raw("  Edit and save: " + _filePath);

    private void PrintWatchGuidance()
    {
        Log.Blank();
        Log.Detail("Save the file to refresh the preview. No project rebuild needed.");
        Log.Detail(_canReadKeys
            ? "Press V for WASDK versions. Ctrl+C to stop."
            : "Use --list to see available WASDK versions. Ctrl+C to stop.");
        PrintEditPath();
    }

    private bool ReadVersionShortcut()
    {
        if (!_canReadKeys)
        {
            return false;
        }

        try
        {
            bool requested = false;
            while (Console.KeyAvailable)
            {
                ConsoleKeyInfo key = Console.ReadKey(intercept: true);
                requested |= key.Key == ConsoleKey.V
                    && (key.Modifiers & (ConsoleModifiers.Control | ConsoleModifiers.Alt)) == 0;
            }

            return requested;
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException)
        {
            _canReadKeys = false;
            Log.Warn("Console keyboard input is unavailable: " + ex.Message);
            PrintWatchGuidance();
            return false;
        }
    }

    private async Task ShowVersionsAsync(CancellationToken ct)
    {
        IReadOnlyList<string>? versions = null;
        string? error = null;
        try
        {
            Log.Detail("Looking up WASDK versions...");
            versions = await GetVersionsAsync().ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex) when (WasdkVersionList.IsExpectedFailure(ex))
        {
            error = ex.Message;
        }

        try
        {
            // Only printing takes the gate; a slow feed must not block live edits or health checks.
            await _gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (versions is not null)
                {
                    try
                    {
                        WasdkVersionList.Print(versions, _layout.CacheRoot, _options.Prerelease, _running?.Version);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        error = ex.Message;
                    }
                }

                if (error is not null)
                {
                    Log.Error("Could not list WASDK versions: " + error);
                }

                if (!_useRuntimeHeaders && _options.Wasdk is { Length: > 0 })
                {
                    Log.Detail("--wasdk overrides the file header; omit it to switch versions by editing.");
                }

                PrintWatchGuidance();
            }
            finally
            {
                _gate.Release();
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Ctrl+C cancels the lookup and any wait to print.
        }
    }

    /// <summary>
    /// Blocks until Ctrl+C, noticing along the way if the runner exits on its own. That is
    /// the interesting case for this tool: a crash on startup is often the bug being chased.
    /// </summary>
    private async Task WatchUntilCancelledAsync(CancellationTokenSource cancellation)
    {
        CancellationToken ct = cancellation.Token;
        Task? versionDisplay = null;
        Task controls = ProcessControlsAsync(ct);
        long lastHealthCheck = Environment.TickCount64;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(_canReadKeys ? KeyboardPollInterval : HealthInterval, ct).ConfigureAwait(false);

                if (versionDisplay is { IsCompleted: true })
                {
                    Task completed = versionDisplay;
                    versionDisplay = null;
                    await completed.ConfigureAwait(false);
                }

                bool requested = ReadVersionShortcut();
                if (requested && versionDisplay is null && !ct.IsCancellationRequested)
                {
                    versionDisplay = ShowVersionsAsync(ct);
                }

                long now = Environment.TickCount64;
                if (now - lastHealthCheck < HealthInterval.TotalMilliseconds)
                {
                    continue;
                }
                lastHealthCheck = now;

                // Skip deliberate relaunches; their process exits are not crashes.
                if (!_gate.Wait(0))
                {
                    continue;
                }

                try
                {
                    if (_screenshotPath is not null && _requestId != _reportedCapture)
                    {
                        if (ReadCurrentCapture() is { } result)
                        {
                            ReportCapture(result);
                        }
                        else if (_running is not null && _host.ProcessId is not null
                            && now >= _captureDeadline && _timedOutCapture != _requestId)
                        {
                            _timedOutCapture = _requestId;
                            Log.Error("The runner did not complete rendering and capture within 60 seconds.");
                            Log.Detail("Expected result: " + _host.ResultPath);
                            Log.Detail("Still watching; save the repro to retry.");
                        }
                    }

                    if (_running is not null && _host.ProcessId is null)
                    {
                        Log.Event("the runner exited on its own");
                        ReportRunnerLog();
                        Log.Detail("Save the file to launch it again.");
                        _running = null;
                        PrintWatchGuidance();
                    }
                }
                finally
                {
                    _gate.Release();
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Normal Ctrl+C shutdown.
        }
        finally
        {
            await cancellation.CancelAsync().ConfigureAwait(false);
            await controls.ConfigureAwait(false);
            if (versionDisplay is not null)
            {
                await versionDisplay.ConfigureAwait(false);
            }
        }
    }

    private static string RunnerLogPath =>
        Path.Combine(Path.GetTempPath(), "winui-repro-app", "runner.log");

    private static long CurrentRunnerLogLength()
    {
        try
        {
            return File.Exists(RunnerLogPath) ? new FileInfo(RunnerLogPath).Length : 0;
        }
        catch (IOException)
        {
            return 0;
        }
    }

    /// <summary>
    /// Prints whatever the runner appended to its crash log since we launched it. Only the
    /// new part, so an old crash from a previous run is not mistaken for this one.
    /// </summary>
    private void ReportRunnerLog()
    {
        string text;
        try
        {
            if (!File.Exists(RunnerLogPath))
            {
                Log.Detail("No runner log at " + RunnerLogPath);
                return;
            }

            using var stream = new FileStream(RunnerLogPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            if (stream.Length <= _logOffset)
            {
                Log.Detail("The runner logged nothing, so it did not crash on a managed exception.");
                return;
            }

            stream.Seek(_logOffset, SeekOrigin.Begin);
            using var reader = new StreamReader(stream);
            text = reader.ReadToEnd();
        }
        catch (IOException ex)
        {
            Log.Detail("Could not read the runner log: " + ex.Message);
            return;
        }

        Log.Blank();
        Log.Raw("--- runner.log ---");
        Log.Raw(text.TrimEnd());
        Log.Raw("------------------");
        Log.Blank();
    }

    /// <summary>
    /// Reads the file, retrying briefly. An editor can still hold the handle for a few
    /// milliseconds after the change notification fires.
    /// </summary>
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
                Thread.Sleep(40 * attempt);
            }
            catch (UnauthorizedAccessException)
            {
                Thread.Sleep(40 * attempt);
            }
        }

        return null;
    }
}
