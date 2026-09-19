namespace ReproStudio_Cli;

/// <summary>
/// The command line, parsed. Options that can also come from the repro file's header are
/// nullable here so "not given" stays distinguishable from "given as false" - the command
/// line only wins when it actually said something.
/// </summary>
public sealed class CliOptions
{
    /// <summary>The repro file, or null to use the bundled sample when launching.</summary>
    public string? File { get; private set; }

    /// <summary>Windows App SDK version, overriding the file's <c>// wasdk:</c> header.</summary>
    public string? Wasdk { get; private set; }

    public string? Sdk { get; private set; }

    /// <summary>WinUI override (a version or a local .nupkg), overriding <c>// winui:</c>.</summary>
    public string? WinUi { get; private set; }

    /// <summary>
    /// Folder of loose files to copy over the runner, overriding <c>// payload:</c>.
    /// When not given, a <c>payload\</c> folder next to the exe is used if it has files.
    /// </summary>
    public string? Payload { get; private set; }

    /// <summary>Package identity, overriding <c>// packaged:</c>. Null when not given.</summary>
    public bool? Packaged { get; private set; }

    /// <summary>Include prerelease versions when listing and resolving.</summary>
    public bool Prerelease { get; private set; }

    /// <summary>Watch the file and re-push on save. On by default.</summary>
    public bool Watch { get; private set; } = true;

    /// <summary>Cloak the Runner window and save a screenshot after each render.</summary>
    public bool Headless { get; private set; }

    /// <summary>PNG output path, relative to the invoking working directory.</summary>
    public string? Screenshot { get; private set; }

    /// <summary>Prepare the runner but do not launch it, then exit.</summary>
    public bool ProvisionOnly { get; private set; }

    /// <summary>List available Windows App SDK versions and exit.</summary>
    public bool List { get; private set; }

    /// <summary>Print environment diagnostics and exit.</summary>
    public bool Doctor { get; private set; }

    /// <summary>Delete provisioned runners before doing anything else.</summary>
    public bool ClearCache { get; private set; }

    /// <summary>Show usage and exit.</summary>
    public bool Help { get; private set; }

    public static string Usage =>
        """
        ReproStudio - run a single-file WinUI repro against any Windows App SDK version.

        usage:
          ReproStudio [file.cs] [options]
          ReproStudio --list [--prerelease]
          ReproStudio --doctor

        Omit file.cs to run the bundled samples\hello.cs, regardless of the current folder.

        from a source checkout:
          dotnet run                         Build both processes and open the hello sample.
          dotnet run -- samples\hello.cs     Edit a source sample in place.
          dotnet run -- --help               Pass host options after "--".

        options:
          --wasdk <version>   Windows App SDK version. Partial is fine ("2.2" picks the
                              newest 2.2). Overrides the file's "// wasdk:" header.
          --winui <ver|path>  Override just the WinUI component: a version, or the path to
                              a local .nupkg. Overrides "// winui:".
          --sdk <ver|match|base>
                              SDK/API used to compile and execute C#. Default: match
                              the native runtime source. A WASDK version selects APIs
                              independently; base keeps the bundled managed APIs.
                              Partial versions resolve like --wasdk. Overrides "// sdk:".
          --payload <dir>     Copy every file in <dir> over the runner, after the Windows
                              App SDK version is laid down. The quickest way to test a
                              private build: drop Microsoft.ui.xaml.dll in and run.
                              Defaults to a "payload" folder next to this exe.
                              Overrides "// payload:".
          --packaged          Run the runner with package identity. Needs Developer Mode.
          --unpackaged        Force no package identity, even if the file asks for it.
          --prerelease        Include prerelease versions when resolving and listing.
          --headless          Cloak the runner window. Save ReproStudio.png in the current
                              folder after each render; --screenshot overrides the path.
          --screenshot <png>  Save a PNG after each render, also in visible mode.
                              Uses Windows.Graphics.Capture, with a reported XAML fallback.
          --no-watch          Launch and exit, instead of watching the file for saves.
                              With --headless, wait for the screenshot and stop the runner.
                              With --screenshot alone, wait for the image and leave it open.
          --provision-only    Prepare the runner for the version asked for, then exit
                              without launching. Warms the cache; also useful for
                              building a bundle that runs with no network.
          --clear-cache       Delete provisioned runners first (downloads are kept).
          --list              List available Windows App SDK versions and exit.
          --doctor            Print environment diagnostics and exit.
          -h, --help          Show this help.

        while watching:
          V                  List WASDK versions without stopping the preview.
          Ctrl+C             Stop the runner and exit.
          The full path to edit is repeated below the console instructions.
          With redirected input, use --list instead of the V shortcut.

        environment:
          REPROSTUDIO_CACHE   Where downloads and provisioned runners go. Defaults to
                              %LOCALAPPDATA%\winui-repro-app.

        repro file header:
          A repro is an ordinary .cs file. Lines at the very top starting with "//" set
          how it runs. Everything is optional.

            // repro:      a friendly name
            // wasdk:      2.2                 Windows App SDK version
            // sdk:        match | base | 2.4.1-experimental
            // winui:      3.0.0-x  |  C:\p.nupkg  |  default
            // packaged:   yes | no            run with package identity
            // theme:      light | dark | default
            // flow:       ltr | rtl
            // dpi:        100 - 400
            // background: #202020
            // win32:      GetWindowRect, GetDpiForWindow

          The markup goes in a "string Xaml = ..." literal so the file stays valid C#.
          For CLI-only setup before XAML initializes, add:

            static void OnProcessLaunch()
            {
                EnableXamlOptionalChange(63530879);
            }

        examples:
          ReproStudio
          ReproStudio --wasdk 2.2
          ReproStudio bug.cs
          ReproStudio bug.cs --wasdk 2.2 --packaged
          ReproStudio bug.cs --headless --no-watch
          ReproStudio bug.cs --headless --screenshot captures\bug.png
          ReproStudio bug.cs --winui C:\builds\Microsoft.WindowsAppSDK.WinUI.3.0.0.nupkg
        """;

    /// <summary>
    /// Parses arguments. Returns false with a message when something is wrong, so the
    /// caller can print the problem and the usage together.
    /// </summary>
    public static bool TryParse(string[] args, out CliOptions options, out string? error)
    {
        ArgumentNullException.ThrowIfNull(args);

        options = new CliOptions();
        error = null;

        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];
            switch (arg.ToLowerInvariant())
            {
                case "-h":
                case "--help":
                case "/?":
                    options.Help = true;
                    break;
                case "--list":
                    options.List = true;
                    break;
                case "--doctor":
                    options.Doctor = true;
                    break;
                case "--prerelease":
                    options.Prerelease = true;
                    break;
                case "--clear-cache":
                    options.ClearCache = true;
                    break;
                case "--no-watch":
                    options.Watch = false;
                    break;
                case "--headless":
                    options.Headless = true;
                    break;
                case "--screenshot":
                    if (!TryTakeValue(args, ref i, out string? screenshot, out error))
                    {
                        return false;
                    }

                    if (string.IsNullOrWhiteSpace(screenshot) || screenshot.StartsWith('-')
                        || !Path.GetExtension(screenshot).Equals(".png", StringComparison.OrdinalIgnoreCase))
                    {
                        error = "--screenshot needs a PNG file path, such as captures\\repro.png.";
                        return false;
                    }

                    options.Screenshot = screenshot;
                    break;
                case "--provision-only":
                    options.ProvisionOnly = true;
                    options.Watch = false;
                    break;
                case "--packaged":
                    options.Packaged = true;
                    break;
                case "--unpackaged":
                    options.Packaged = false;
                    break;
                case "--wasdk":
                    if (!TryTakeValue(args, ref i, out string? wasdk, out error))
                    {
                        return false;
                    }

                    options.Wasdk = wasdk;
                    break;
                case "--winui":
                    if (!TryTakeValue(args, ref i, out string? winui, out error))
                    {
                        return false;
                    }

                    options.WinUi = winui;
                    break;
                case "--sdk":
                    if (!TryTakeValue(args, ref i, out string? sdk, out error)) return false;
                    try { options.Sdk = ReproStudio.Shared.SdkSelection.Normalize(sdk); }
                    catch (ArgumentException ex) { error = ex.Message; return false; }
                    break;
                case "--payload":
                    if (!TryTakeValue(args, ref i, out string? payload, out error))
                    {
                        return false;
                    }

                    options.Payload = payload;
                    break;
                default:
                    if (arg.StartsWith('-'))
                    {
                        error = "Unknown option: " + arg;
                        return false;
                    }

                    if (options.File is not null)
                    {
                        error = "More than one file given: " + options.File + " and " + arg;
                        return false;
                    }

                    options.File = arg;
                    break;
            }
        }

        return true;
    }

    private static bool TryTakeValue(string[] args, ref int i, out string? value, out string? error)
    {
        if (i + 1 >= args.Length || string.IsNullOrWhiteSpace(args[i + 1])
            || args[i + 1].StartsWith('-') || args[i + 1] == "/?")
        {
            value = null;
            error = args[i] + " needs a value.";
            return false;
        }

        value = args[++i];
        error = null;
        return true;
    }
}
