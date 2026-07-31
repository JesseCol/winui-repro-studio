using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
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
