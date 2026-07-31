using System;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using ReproStudio_Host.ViewModels;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.System;
using Windows.UI.Core;

using ReproStudio.Shared;

namespace ReproStudio_Host;

/// <summary>
/// The host page: a XAML editor and a C# editor. Edits are pushed to the runner,
/// which shows the live preview in its own window.
/// </summary>
public sealed partial class MainPage : Page
{
    private bool _syncingEditors;
    private bool _pickerOpen;

    public MainPage()
    {
        InitializeComponent();
        ViewModel = new MainViewModel(DispatcherQueue);
        ViewModel.PropertyChanged += OnViewModelPropertyChanged;
        Loaded += OnLoaded;
    }

    public MainViewModel ViewModel { get; }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        XamlEditor.Text = ViewModel.XamlText;
        CSharpEditor.Text = ViewModel.CSharpText;
        if (App.StartupFilePath is string startupFile)
        {
            ViewModel.SetStartupFile(startupFile);
        }

        ViewModel.Start();
    }

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        // When a watched file drives the editors, mirror its content back into them.
        // The _syncingEditors guard keeps the resulting TextChanged from writing back
        // into the view model (re-entrantly setting TextBox.Text can hang WinUI).
        if (e.PropertyName == nameof(MainViewModel.XamlText) && XamlEditor.Text != ViewModel.XamlText)
        {
            _syncingEditors = true;
            XamlEditor.Text = ViewModel.XamlText;
            _syncingEditors = false;
        }
        else if (e.PropertyName == nameof(MainViewModel.CSharpText) && CSharpEditor.Text != ViewModel.CSharpText)
        {
            _syncingEditors = true;
            CSharpEditor.Text = ViewModel.CSharpText;
            _syncingEditors = false;
        }
    }

    private void XamlEditor_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_syncingEditors)
        {
            return;
        }

        ViewModel.XamlText = XamlEditor.Text;
    }

    private void CSharpEditor_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_syncingEditors)
        {
            return;
        }

        ViewModel.CSharpText = CSharpEditor.Text;
    }

    private async void BrowseWinUi_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker();
        picker.FileTypeFilter.Add(".nupkg");

        // WinUI 3 desktop pickers must be parented to the window's HWND.
        nint hwnd = WinRT.Interop.WindowNative.GetWindowHandle(MainWindow.Instance);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

        StorageFile? file = await picker.PickSingleFileAsync();
        if (file is not null)
        {
            ViewModel.AddLocalWinUiPackage(file.Path);
        }
    }

    private async void OpenFile_Click(object sender, RoutedEventArgs e)
    {
        // Only one file picker at a time. Opening a second while one is pending
        // throws E_ACCESSDENIED in WinRT and can crash the app.
        if (_pickerOpen)
        {
            return;
        }

        _pickerOpen = true;
        try
        {
            var picker = new FileOpenPicker();
            picker.FileTypeFilter.Add(".cs");

            // WinUI 3 desktop pickers must be parented to the window's HWND.
            nint hwnd = WinRT.Interop.WindowNative.GetWindowHandle(MainWindow.Instance);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

            StorageFile? file = await picker.PickSingleFileAsync();
            if (file is not null)
            {
                ViewModel.OpenExternalFile(file.Path);
            }
        }
#pragma warning disable CA1031 // Log any picker/open failure instead of crashing.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            HostLog.Log("OpenFile_Click failed: " + ex);
        }
        finally
        {
            _pickerOpen = false;
        }
    }

    private async void RelaunchRunner_Click(object sender, RoutedEventArgs e)
    {
        await ViewModel.RelaunchRunnerAsync();
    }

    private async void ClearCache_Click(object sender, RoutedEventArgs e)
    {
        await ViewModel.ClearCacheAsync();
    }

    private void Editor_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Tab || sender is not TextBox textBox)
        {
            return;
        }

        if (textBox.IsReadOnly)
        {
            // In external-file mode the editors are a read-only mirror; let Tab move focus.
            return;
        }

        bool shiftDown =
            (InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift) & CoreVirtualKeyStates.Down)
            == CoreVirtualKeyStates.Down;
        if (shiftDown)
        {
            // Let Shift+Tab move focus out of the editor.
            return;
        }

        const string indent = "    ";
        int start = textBox.SelectionStart;
        textBox.Text = textBox.Text.Remove(start, textBox.SelectionLength).Insert(start, indent);
        textBox.SelectionStart = start + indent.Length;
        e.Handled = true;
    }
}
