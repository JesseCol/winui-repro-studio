namespace ReproStudio_Cli;

/// <summary>
/// The command line, parsed. Options that can also come from the repro file's header are
/// nullable here so "not given" stays distinguishable from "given as false" - the command
/// line only wins when it actually said something.
/// </summary>
public sealed class CliOptions
{
    /// <summary>The repro <c>.cs</c> file to run. Null for <c>--list</c>, <c>--doctor</c> and help.</summary>
    public string? File { get; private set; }

    /// <summary>Windows App SDK version, overriding the file's <c>// wasdk:</c> header.</summary>
    public string? Wasdk { get; private set; }

    /// <summary>WinUI override (a version or a local .nupkg), overriding <c>// winui:</c>.</summary>
    public string? WinUi { get; private set; }

    /// <summary>Package identity, overriding <c>// packaged:</c>. Null when not given.</summary>
    public bool? Packaged { get; private set; }

    /// <summary>Include prerelease versions when listing and resolving.</summary>
    public bool Prerelease { get; private set; }

    /// <summary>Watch the file and re-push on save. On by default.</summary>
    public bool Watch { get; private set; } = true;

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
          ReproStudio <file.cs> [options]
          ReproStudio --list [--prerelease]
          ReproStudio --doctor

        options:
          --wasdk <version>   Windows App SDK version. Partial is fine ("1.6" picks the
                              newest 1.6). Overrides the file's "// wasdk:" header.
          --winui <ver|path>  Override just the WinUI component: a version, or the path to
                              a local .nupkg. Overrides "// winui:".
          --packaged          Run the runner with package identity. Needs Developer Mode.
          --unpackaged        Force no package identity, even if the file asks for it.
          --prerelease        Include prerelease versions when resolving and listing.
          --no-watch          Launch and exit, instead of watching the file for saves.
          --clear-cache       Delete provisioned runners first (downloads are kept).
          --list              List available Windows App SDK versions and exit.
          --doctor            Print environment diagnostics and exit.
          -h, --help          Show this help.

        environment:
          REPROSTUDIO_CACHE   Where downloads and provisioned runners go. Defaults to
                              %LOCALAPPDATA%\winui-repro-app.

        repro file header:
          A repro is an ordinary .cs file. Lines at the very top starting with "//" set
          how it runs. Everything is optional.

            // repro:      a friendly name
            // wasdk:      1.6                 Windows App SDK version
            // winui:      3.0.0-x  |  C:\p.nupkg  |  default
            // packaged:   yes | no            run with package identity
            // theme:      light | dark | default
            // flow:       ltr | rtl
            // dpi:        100 - 400
            // background: #202020
            // topmost:    yes | no

          The markup goes in a "string Xaml = ..." literal so the file stays valid C#.

        examples:
          ReproStudio bug.cs
          ReproStudio bug.cs --wasdk 1.7 --packaged
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

        if (options.File is null && !options.Help && !options.List && !options.Doctor)
        {
            error = args.Length == 0 ? null : "No repro file given.";
            options.Help = true;
            return error is null;
        }

        return true;
    }

    private static bool TryTakeValue(string[] args, ref int i, out string? value, out string? error)
    {
        if (i + 1 >= args.Length)
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
