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
        if (!App.TryParseCaptureOptions(commandLine, out bool isHeadless, out string? screenshotPath, out string? error))
        {
            CrashLog.Log("Runner arguments: " + error);
            Environment.ExitCode = 1;
            return;
        }

        if (isHeadless)
        {
            CrashLog.Log("Headless mode cloaks only the Runner HWND. Windows created by snippet code are outside this scope.");
        }

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
            _ = new App(isHeadless, screenshotPath);
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
    private readonly bool _isHeadless;
    private readonly string? _screenshotPath;
    private Window? _window;

    public App(bool isHeadless = false, string? screenshotPath = null)
    {
        _isHeadless = isHeadless;
        _screenshotPath = screenshotPath;
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
        try
        {
            _window = new MainWindow(requestPath, bounds, _isHeadless, _screenshotPath);
            if (_isHeadless)
            {
                // The constructor cloaks and verifies the HWND before running any Setup.
                // Show without activation, rather than hiding/minimizing or briefly activating.
                _window.AppWindow.Show(false);
                WindowCaptureInterop.VerifyAppCloak(WinRT.Interop.WindowNative.GetWindowHandle(_window));
            }
            else
            {
                _window.Activate();
            }
        }
        catch (Exception ex) when (_isHeadless && CaptureFailures.IsExpected(ex))
        {
            CrashLog.Log("Headless startup failed (not showing a visible window): " + ex);
            Environment.ExitCode = 1;
            _window?.Close();
            Exit();
        }
    }

    internal static bool TryParseCaptureOptions(
        string[] args, out bool isHeadless, out string? screenshotPath, out string? error)
    {
        isHeadless = false;
        screenshotPath = null;
        error = null;
        for (int i = 1; i < args.Length; i++)
        {
            if (string.Equals(args[i], "--headless", StringComparison.OrdinalIgnoreCase))
            {
                isHeadless = true;
            }
            else if (string.Equals(args[i], "--screenshot", StringComparison.OrdinalIgnoreCase))
            {
                if (screenshotPath is not null || i + 1 == args.Length || args[i + 1].StartsWith("--", StringComparison.Ordinal))
                {
                    error = "--screenshot requires exactly one absolute PNG path.";
                    return false;
                }

                screenshotPath = args[++i];
            }
        }

        if (isHeadless && screenshotPath is null)
        {
            error = "--headless requires --screenshot <absolute-path.png>.";
        }
        else if (screenshotPath is not null
            && (!Path.IsPathFullyQualified(screenshotPath)
                || !string.Equals(Path.GetExtension(screenshotPath), ".png", StringComparison.OrdinalIgnoreCase)
                || screenshotPath.IndexOfAny(Path.GetInvalidPathChars()) >= 0))
        {
            error = "--screenshot must be an absolute path ending in .png.";
        }
        else if (screenshotPath is not null
            && (string.IsNullOrWhiteSpace(ParseRequestPath(args))
                || ParseRequestPath(args)!.StartsWith("--", StringComparison.Ordinal)))
        {
            error = "Screenshot capture requires --request <path>.";
        }

        return error is null;
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
