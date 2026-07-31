using System.Runtime.InteropServices;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace ReproStudio_Host;

/// <summary>
/// The application window. This hosts a Frame that displays pages. Add your
/// UI and logic to MainPage.xaml / MainPage.xaml.cs instead of here so you
/// can use Page features such as navigation events and the Loaded lifecycle.
/// </summary>
public sealed partial class MainWindow : Window
{
    // Start narrow: one editor tab is on screen at a time, and the runner window
    // sits flush to our right, so the host doesn't need much width.
    private const int InitialWidthDip = 820;
    private const int InitialHeightDip = 860;

    public MainWindow()
    {
        InitializeComponent();
        Instance = this;

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);

        ApplyBestBackdrop();

        AppWindow.SetIcon("Assets/AppIcon.ico");
        ResizeToInitialSize();

        // Navigate the root frame to the main page on startup.
        RootFrame.Navigate(typeof(MainPage));

        Closed += OnClosed;
    }

    /// <summary>The single host window, used to parent file pickers.</summary>
    public static MainWindow? Instance { get; private set; }

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(nint hwnd);

    /// <summary>
    /// Picks the best backdrop the OS actually supports. This tool runs down to Windows 10
    /// 1809 to chase downlevel bugs, and Mica is Windows 11 only, so the backdrop cannot be
    /// hardcoded in XAML. Falling all the way through leaves no backdrop at all, in which
    /// case the root grid needs an opaque background of its own - the title bar is extended
    /// into the client area, so an unpainted window would show through.
    /// </summary>
    private void ApplyBestBackdrop()
    {
        if (MicaController.IsSupported())
        {
            SystemBackdrop = new MicaBackdrop();
        }
        else if (DesktopAcrylicController.IsSupported())
        {
            SystemBackdrop = new DesktopAcrylicBackdrop();
        }
        else
        {
            RootGrid.Background = (Brush)Application.Current.Resources["ApplicationPageBackgroundThemeBrush"];
        }
    }

    /// <summary>Sizes the window to its initial size, scaled for the current DPI.</summary>
    private void ResizeToInitialSize()
    {
        nint hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        double scale = GetDpiForWindow(hwnd) / 96.0;
        AppWindow.Resize(new SizeInt32(
            (int)(InitialWidthDip * scale),
            (int)(InitialHeightDip * scale)));
    }

    private void OnClosed(object sender, WindowEventArgs args)
    {
        // Shut down the runner process when the host window closes.
        (RootFrame.Content as MainPage)?.ViewModel.Shutdown();
    }
}
