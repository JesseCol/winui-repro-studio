namespace ReproStudio.Shared;

/// <summary>
/// Works out where the base runner comes from and where the app is allowed to write.
/// <para>
/// There are two ways the app gets deployed, and they only differ in where the base
/// runner lives:
/// </para>
/// <list type="bullet">
/// <item><description>
/// <b>Portable (xcopy).</b> A <c>runner-base</c> folder sits next to the host exe, so
/// the whole tool is one folder you unzip on any machine and double-click.
/// </description></item>
/// <item><description>
/// <b>Dev.</b> No <c>runner-base</c> next to the exe (the host runs out of
/// <c>bin\...</c>), so we fall back to the one under <c>%LOCALAPPDATA%</c> that a
/// developer built and copied there.
/// </description></item>
/// </list>
/// <para>
/// Either way, everything the app <em>writes</em> (downloaded packages, provisioned
/// per-version runners) stays under <c>%LOCALAPPDATA%</c>. The bundle folder is only
/// ever read, so it can live on a share, a USB stick, or anywhere else the user has
/// no write access.
/// </para>
/// </summary>
public sealed class AppLayout
{
    /// <summary>The folder name holding the prebuilt self-contained runner.</summary>
    public const string BaseRunnerFolderName = "runner-base";

    /// <summary>
    /// Environment variable that moves the writable cache somewhere else. Useful when
    /// %LOCALAPPDATA% is small or roamed, and for testing a clean-machine run without
    /// touching the real cache.
    /// </summary>
    public const string CacheRootVariable = "REPROSTUDIO_CACHE";

    private AppLayout(string cacheRoot, string baseRunnerDir, bool isPortable)
    {
        CacheRoot = cacheRoot;
        BaseRunnerDir = baseRunnerDir;
        IsPortable = isPortable;
    }

    /// <summary>Writable root for downloaded packages and provisioned runners.</summary>
    public string CacheRoot { get; }

    /// <summary>The base runner to copy per version. Read only; may not exist yet.</summary>
    public string BaseRunnerDir { get; }

    /// <summary>True when the base runner was found next to the host exe (xcopy bundle).</summary>
    public bool IsPortable { get; }

    /// <summary>True when a usable base runner is actually on disk.</summary>
    public bool HasBaseRunner => Directory.Exists(BaseRunnerDir);

    /// <summary>
    /// Finds the base runner, preferring one shipped next to the host exe over the
    /// developer one under <c>%LOCALAPPDATA%</c>.
    /// </summary>
    /// <param name="appDirectory">
    /// The host's own folder. Defaults to <see cref="AppContext.BaseDirectory"/>.
    /// </param>
    /// <param name="cacheRoot">
    /// The writable cache root. Defaults to <c>%REPROSTUDIO_CACHE%</c> when that is set,
    /// otherwise <c>%LOCALAPPDATA%\winui-repro-app</c>.
    /// </param>
    public static AppLayout Resolve(string? appDirectory = null, string? cacheRoot = null)
    {
        appDirectory ??= AppContext.BaseDirectory;
        cacheRoot ??= Environment.GetEnvironmentVariable(CacheRootVariable) is { Length: > 0 } custom
            ? Path.GetFullPath(custom)
            : Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "winui-repro-app");

        string bundled = Path.Combine(appDirectory, BaseRunnerFolderName);
        return Directory.Exists(bundled)
            ? new AppLayout(cacheRoot, bundled, isPortable: true)
            : new AppLayout(cacheRoot, Path.Combine(cacheRoot, BaseRunnerFolderName), isPortable: false);
    }

    /// <summary>
    /// Explains what to do when <see cref="HasBaseRunner"/> is false. The fix differs by
    /// deployment: a broken bundle is missing a folder, whereas a dev box has simply not
    /// built the base yet.
    /// </summary>
    public string DescribeMissingBaseRunner() =>
        $"Base runner missing (looked in {BaseRunnerDir}). "
        + "In an xcopy bundle, the runner-base folder should sit next to the host exe - "
        + "re-extract the zip. On a dev box, run pack.ps1, or build "
        + "src\\ReproStudio.Runner and copy its output to "
        + $"%LOCALAPPDATA%\\winui-repro-app\\{BaseRunnerFolderName}.";
}
