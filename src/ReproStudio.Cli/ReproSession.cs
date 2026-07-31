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
internal sealed class ReproSession : IDisposable
{
    /// <summary>How long to wait after a file change, so one save is one reload.</summary>
    private static readonly TimeSpan SaveDebounce = TimeSpan.FromMilliseconds(150);

    /// <summary>How often to notice that the runner died on its own.</summary>
    private static readonly TimeSpan HealthInterval = TimeSpan.FromSeconds(1);

    private readonly CliOptions _options;
    private readonly AppLayout _layout;
    private readonly string _filePath;

    private readonly HttpClient _http;
    private readonly RunnerProvisioner _provisioner;
    private readonly RunnerHost _host;

    /// <summary>Serialises reloads against each other and against the health check.</summary>
    private readonly SemaphoreSlim _gate = new(1, 1);

    private FileSystemWatcher? _watcher;
    private System.Threading.Timer? _debounce;
    private CancellationToken _ct;

    /// <summary>WASDK versions from NuGet, fetched at most once per run.</summary>
    private IReadOnlyList<string>? _versions;

    /// <summary>
    /// The last version we told the user a partial token resolved to. Watch mode
    /// re-resolves on every save, and repeating the same line each time is noise.
    /// </summary>
    private string? _lastResolvedLogged;

    /// <summary>What the running runner was launched with, or null if none is running.</summary>
    private LaunchPlan? _running;

    /// <summary>Size of runner.log when we launched, so a crash report shows only new lines.</summary>
    private long _logOffset;

    public ReproSession(CliOptions options, AppLayout layout)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(layout);

        _options = options;
        _layout = layout;
        _filePath = Path.GetFullPath(options.File!);

        // Generous timeout: a cold cache downloads tens of megabytes of NuGet packages,
        // and the default 100 seconds is not enough on a slow or throttled link.
        _http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        _provisioner = new RunnerProvisioner(_http, layout.CacheRoot);
        _host = new RunnerHost(new PackagedRunnerLauncher());
    }

    /// <summary>Everything that forces a new runner process when it changes.</summary>
    private readonly record struct LaunchPlan(string Version, string WinUiKey, bool Packaged, int Dpi)
    {
        public string Describe() => Version
            + (WinUiKey.Length == 0 ? string.Empty : " + winui " + WinUiKey)
            + (Packaged ? " (packaged)" : string.Empty);
    }

    /// <summary>Runs the repro. Returns a process exit code.</summary>
    public async Task<int> RunAsync(CancellationToken ct)
    {
        _ct = ct;

        if (!File.Exists(_filePath))
        {
            Log.Error("No such file: " + _filePath);
            return 1;
        }

        if (!_layout.HasBaseRunner)
        {
            Log.Error(_layout.DescribeMissingBaseRunner());
            return 1;
        }

        Log.Field("file", _filePath);
        Log.Field("cache", _layout.CacheRoot);
        Log.Field("runner", _layout.BaseRunnerDir, _layout.IsPortable ? "(portable)" : "(dev)");

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
            Log.Blank();
            Log.Ok("Runner left running. Re-run to pick up edits.");
            return 0;
        }

        Log.Step("watching");
        Log.Detail("Save the file to push changes. Ctrl+C to stop.");
        StartWatching();

        await WatchUntilCancelledAsync(ct).ConfigureAwait(false);

        Log.Blank();
        Log.Ok("Stopped.");
        return 0;
    }

    public void Dispose()
    {
        _watcher?.Dispose();
        _debounce?.Dispose();
        _gate.Dispose();

        // In no-watch mode the runner is meant to outlive us, so leave it alone.
        if (_options.Watch)
        {
            _host.Dispose();
        }

        _http.Dispose();
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

        if (!parsed.HasXaml)
        {
            Log.Warn("No 'string Xaml = ...' literal found, so there is nothing to render.");
        }

        LaunchPlan plan;
        WinUiOverride? winui;
        try
        {
            winui = ResolveWinUi(parsed.WinUiToken);
            string version = await ResolveVersionAsync(parsed.WasdkVersion).ConfigureAwait(false);
            plan = new LaunchPlan(
                version,
                winui?.CacheKey ?? string.Empty,
                _options.Packaged ?? parsed.Packaged ?? false,
                parsed.Dpi ?? 100);
        }
        catch (Exception ex) when (ex is IOException or HttpRequestException or InvalidOperationException or TaskCanceledException)
        {
            Log.Error(ex.Message);
            return false;
        }

        Snippet snippet = BuildSnippet(parsed, plan);
        _host.WriteRequest(snippet);

        if (_running == plan)
        {
            Log.Event("pushed" + (parsed.Title is { Length: > 0 } t ? "  " + t : string.Empty));
            return true;
        }

        if (!firstRun)
        {
            Log.Event("relaunching: " + plan.Describe());
        }

        return await ProvisionAndLaunchAsync(plan, winui, firstRun).ConfigureAwait(false);
    }

    private async Task<bool> ProvisionAndLaunchAsync(LaunchPlan plan, WinUiOverride? winui, bool firstRun)
    {
        if (firstRun)
        {
            Log.Step("provision");
            Log.Field("wasdk", plan.Version);
            if (winui is not null)
            {
                Log.Field("winui", winui.LocalNupkgPath ?? winui.NuGetVersion ?? "default");
            }

            Log.Field("packaged", plan.Packaged ? "yes" : "no");
        }

        string exe;
        try
        {
            var progress = new Progress<ProvisionProgress>(p => Log.Detail(p.Message));
            exe = await _provisioner
                .EnsureRunnerAsync(plan.Version, _layout.BaseRunnerDir, winui, progress, _ct)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            Log.Error("Could not prepare a runner for " + plan.Version + ": " + ex.Message);
            Log.Detail("Run with --list to see the versions NuGet actually has.");
            return false;
        }

        if (firstRun)
        {
            Log.Ok("ready: " + exe);
            Log.Step("launch");
        }

        _logOffset = CurrentRunnerLogLength();

        RunnerHost.LaunchResult result = await _host.LaunchAsync(exe, bounds: null, plan.Packaged).ConfigureAwait(false);
        if (!result.Launched)
        {
            Log.Error("The runner did not start." + result.ModeNote);
            ReportRunnerLog();
            _running = null;
            return false;
        }

        // A packaged launch can silently fall back to unpackaged, which changes what is
        // actually under test. Say so loudly rather than letting it pass as success.
        if (plan.Packaged && !result.ModeNote.Contains("packaged)", StringComparison.Ordinal))
        {
            Log.Warn("Requested packaged, but" + result.ModeNote);
            Log.Detail("Run --doctor to check Developer Mode.");
        }

        _running = plan;

        string mode = result.ModeNote.Trim();
        if (firstRun)
        {
            Log.Ok("running" + (mode.Length == 0 ? string.Empty : " " + mode) + ", pid " + _host.ProcessId);
        }
        else
        {
            Log.Event("running " + plan.Describe());
        }

        return true;
    }

    private Snippet BuildSnippet(ParsedSnippetFile parsed, LaunchPlan plan) => new()
    {
        Title = parsed.Title,
        WasdkVersion = plan.Version,
        Dpi = plan.Dpi,
        Theme = parsed.Theme,
        FlowDirection = parsed.FlowDirection,
        Background = parsed.Background,
        Topmost = parsed.Topmost,
        Xaml = parsed.Xaml,
        CSharp = parsed.CSharp,
    };

    /// <summary>
    /// Turns a version token into a real version. A fully written version is used as-is so
    /// a pinned repro file works with no network at all; anything shorter (or missing) needs
    /// the list from NuGet.
    /// </summary>
    private async Task<string> ResolveVersionAsync(string? headerToken)
    {
        string? token = _options.Wasdk ?? headerToken;

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

    private async Task<IReadOnlyList<string>?> TryListVersionsAsync()
    {
        if (_versions is not null)
        {
            return _versions;
        }

        try
        {
            _versions = await _provisioner.ListWasdkVersionsAsync(_options.Prerelease, _ct).ConfigureAwait(false);
            return _versions;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
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
        string? token = _options.WinUi ?? headerToken;
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
    }

    /// <summary>Restarts the debounce, so a burst of events becomes one reload.</summary>
    private void Schedule() => _debounce?.Change(SaveDebounce, Timeout.InfiniteTimeSpan);

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
            _gate.Release();
        }
    }

    /// <summary>
    /// Blocks until Ctrl+C, noticing along the way if the runner exits on its own. That is
    /// the interesting case for this tool: a crash on startup is often the bug being chased.
    /// </summary>
    private async Task WatchUntilCancelledAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(HealthInterval, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            // Skip the check while a reload is in flight, otherwise the deliberate kill
            // in the middle of a relaunch looks like a crash.
            if (!_gate.Wait(0))
            {
                continue;
            }

            try
            {
                if (_running is not null && _host.ProcessId is null)
                {
                    Log.Event("the runner exited on its own");
                    ReportRunnerLog();
                    Log.Detail("Save the file to launch it again.");
                    _running = null;
                }
            }
            finally
            {
                _gate.Release();
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
