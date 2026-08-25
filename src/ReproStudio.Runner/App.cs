using System;
using System.Threading;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.XamlTypeInfo;
using ReproStudio.Shared;
using ReproStudio_Runner.Services;

namespace ReproStudio_Runner;

/// <summary>
/// Hand-written entry point. The XAML compiler normally generates this, but the
/// runner has no XAML, so we replicate it: init COM wrappers, then start the
/// WinUI application with a dispatcher-backed synchronization context.
/// </summary>
public static class Program
{
    [STAThread]
    private static void Main()
    {
        WinRT.ComWrappersSupport.InitializeComWrappers();
        string[] commandLine = Environment.GetCommandLineArgs();
        if (HasArgument(commandLine, "--run-process-launch"))
        {
            try
            {
                ProcessLaunchEngine.Run(App.ParseRequestPath(commandLine)!);
            }
            catch (Exception ex)
            {
                CrashLog.Log($"{ProcessLaunchMethod.Name} failed: {ex}");
                Environment.ExitCode = 1;
                return;
            }
        }

        Application.Start((p) =>
        {
            var context = new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread());
            SynchronizationContext.SetSynchronizationContext(context);
            _ = new App();
        });
    }

    private static bool HasArgument(string[] args, string value) =>
        args.Any(arg => string.Equals(arg, value, StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// Code-only WinUI application (no App.xaml). Building the runner without compiled
/// XAML means it carries no WASDK version stamp, so a single build can host the
/// runtime DLLs of different WASDK versions copied next to the exe. We replicate
/// what the XAML compiler would have generated for us:
///   - register XamlControlsResources (the default control styles) in code, and
///   - implement IXamlMetadataProvider by delegating to the WinUI controls
///     provider so XamlReader.Load can resolve built-in controls like Button.
/// </summary>
public partial class App : Application, IXamlMetadataProvider
{
    private readonly XamlControlsXamlMetaDataProvider _provider = new();
    private Window? _window;

    public App()
    {
        UnhandledException += (s, e) =>
        {
            CrashLog.Log("UnhandledException: " + e.Message + Environment.NewLine + e.Exception);
            e.Handled = true;
        };

        AppDomain.CurrentDomain.UnhandledException += (s, e) =>
            CrashLog.Log("AppDomain.UnhandledException: " + e.ExceptionObject);
    }

    public IXamlType GetXamlType(Type type) => _provider.GetXamlType(type);

    public IXamlType GetXamlType(string fullName) => _provider.GetXamlType(fullName);

    public XmlnsDefinition[] GetXmlnsDefinitions() => _provider.GetXmlnsDefinitions();

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        // App.xaml would have merged this in; do it by hand. It points at
        // ms-appx:///Microsoft.UI.Xaml.Controls/... which MRT resolves to the
        // Microsoft.UI.Xaml.Controls.pri sitting next to the exe.
        Resources.MergedDictionaries.Add(new XamlControlsResources());

        string[] commandLine = Environment.GetCommandLineArgs();
        string? requestPath = ParseRequestPath(commandLine);
        RunnerBounds? bounds = ParseBounds(commandLine);
        _window = new MainWindow(requestPath, bounds);
        _window.Activate();
    }

    internal static string? ParseRequestPath(string[] args)
    {
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], "--request", StringComparison.OrdinalIgnoreCase))
            {
                return args[i + 1];
            }
        }

        return null;
    }

    private static RunnerBounds? ParseBounds(string[] args)
    {
        for (int i = 0; i < args.Length - 4; i++)
        {
            if (!string.Equals(args[i], "--bounds", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (int.TryParse(args[i + 1], out int x)
                && int.TryParse(args[i + 2], out int y)
                && int.TryParse(args[i + 3], out int width)
                && int.TryParse(args[i + 4], out int height))
            {
                return new RunnerBounds(x, y, width, height);
            }
        }

        return null;
    }
}

/// <summary>Screen bounds (physical pixels) the host wants the runner window placed at.</summary>
public readonly record struct RunnerBounds(int X, int Y, int Width, int Height);
