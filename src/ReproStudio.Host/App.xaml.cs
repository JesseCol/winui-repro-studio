using System;
using System.IO;
using Windows.ApplicationModel;
using Windows.ApplicationModel.Activation;
using Windows.Foundation;
using Windows.Foundation.Collections;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using Microsoft.UI.Xaml.Shapes;
using Microsoft.Windows.AppLifecycle;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

using ReproStudio.Shared;

namespace ReproStudio_Host;

/// <summary>
/// Provides application-specific behavior to supplement the default Application class.
/// </summary>
public partial class App : Application
{
    private Window? _window;

    /// <summary>
    /// Initializes the singleton application object.  This is the first line of authored code
    /// executed, and as such is the logical equivalent of main() or WinMain().
    /// </summary>
    public App()
    {
        InitializeComponent();
        UnhandledException += OnUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (s, e) =>
            HostLog.Log("APPDOMAIN-UNHANDLED: " + e.ExceptionObject);
        System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (s, e) =>
        {
            HostLog.Log("TASK-UNOBSERVED: " + e.Exception);
            e.SetObserved();
        };
    }

    /// <summary>
    /// A repro file passed with <c>--file &lt;path&gt;</c> at launch, so an agent can
    /// point the app at a file without clicking the picker. Null when not supplied.
    /// </summary>
    public static string? StartupFilePath { get; private set; }

    /// <summary>
    /// Invoked when the application is launched.
    /// </summary>
    /// <param name="args">Details about the launch request and process.</param>
    protected override void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
    {
        StartupFilePath = ResolveStartupFile();
        _window = new MainWindow();
        _window.Activate();
    }

    /// <summary>
    /// Finds a <c>--file &lt;path&gt;</c> argument. Checks the process command line
    /// (quote-aware) first, then the Windows App SDK launch activation arguments,
    /// since a packaged app can receive its arguments through either channel.
    /// </summary>
    private static string? ResolveStartupFile()
    {
        string? raw = FromCommandLine() ?? FromActivation();
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        try
        {
            string full = System.IO.Path.GetFullPath(raw.Trim().Trim('"'));
            if (File.Exists(full))
            {
                return full;
            }

            HostLog.Log($"--file not found: '{raw}' -> '{full}'");
        }
#pragma warning disable CA1031 // A bad path must not stop the app from starting.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            HostLog.Log($"--file resolve failed for '{raw}': {ex.Message}");
        }

        return null;
    }

    private static string? FromCommandLine()
    {
        string[] argv = Environment.GetCommandLineArgs();
        for (int i = 0; i < argv.Length - 1; i++)
        {
            if (string.Equals(argv[i], "--file", StringComparison.OrdinalIgnoreCase))
            {
                return argv[i + 1];
            }
        }

        return null;
    }

    private static string? FromActivation()
    {
        try
        {
            AppActivationArguments activation = Microsoft.Windows.AppLifecycle.AppInstance.GetCurrent().GetActivatedEventArgs();
            if (activation?.Data is ILaunchActivatedEventArgs launch
                && !string.IsNullOrEmpty(launch.Arguments))
            {
                const string flag = "--file";
                int idx = launch.Arguments.IndexOf(flag, StringComparison.OrdinalIgnoreCase);
                if (idx >= 0)
                {
                    return launch.Arguments[(idx + flag.Length)..].Trim();
                }
            }
        }
#pragma warning disable CA1031 // Activation arg parsing is best-effort.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            HostLog.Log("Activation arg read failed: " + ex.Message);
        }

        return null;
    }

    private static void OnUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        HostLog.Log("UNHANDLED: " + e.Exception);
    }
}
