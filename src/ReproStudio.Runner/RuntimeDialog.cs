using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using ReproStudio.Shared;
using ReproStudio_Runner.Services;
using Windows.Storage.Pickers;

namespace ReproStudio_Runner;

/// <summary>Exactly one choice. The host owns all feed, source and provisioning work.</summary>
internal sealed class RuntimeDialog : ContentDialog, IDisposable
{
    private readonly Snippet _snippet;
    private readonly RunnerControlClient _client;
    private readonly nint _hwnd;
    private readonly Action<string> _reportError;
    private readonly CancellationTokenSource _lifetime;
    private readonly RadioButton _wasdk = new() { Content = "Windows App SDK", GroupName = "RuntimeKind" };
    private readonly RadioButton _winui = new() { Content = "WinUI", GroupName = "RuntimeKind" };
    private readonly AutoSuggestBox _search = new() { PlaceholderText = "Search versions" };
    private readonly CheckBox _prerelease = new() { Content = "Include prereleases" };
    private readonly ListView _versions = new() { SelectionMode = ListViewSelectionMode.Single, Height = 144 };
    private readonly Button _browse = new() { Content = "Browse for WinUI .nupkg..." };
    private readonly TextBlock _local = new() { TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap };
    private readonly StackPanel _panel = new() { Spacing = 8, MinWidth = 320 };
    private readonly ContentControl _body = new();
    private readonly ComboBox _sdkMode = new() { Header = "SDK / C# API", HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly AutoSuggestBox _sdkSearch = new() { PlaceholderText = "Search or enter a Windows App SDK API version", Visibility = Visibility.Collapsed };
    private readonly ScrollViewer _scroll = new() { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
    private readonly ScrollViewer _statusScroll = new()
    {
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        MaxHeight = 96,
    };
    private readonly Grid _content = new() { RowSpacing = 8 };
    private CancellationTokenSource? _sdkLookup;
    private string[] _sdkVersions = [];
    private CancellationTokenSource? _lookup;
    private string[] _allVersions = [];
    private string? _localPath;
    private bool _isApplying;
    private bool _isBrowsing;
    private bool _isClosed;

    public RuntimeDialog(Snippet snippet, RunnerControlClient client, nint hwnd, CancellationToken ct, Action<string> reportError)
    {
        _snippet = snippet;
        _client = client;
        _hwnd = hwnd;
        _reportError = reportError;
        _lifetime = CancellationTokenSource.CreateLinkedTokenSource(ct);
        Title = "SDK / API and native runtime";
        PrimaryButtonText = "Apply and restart";
        CloseButtonText = "Cancel";
        DefaultButton = ContentDialogButton.Close;
        IsPrimaryButtonEnabled = false;
        MainWindow.SetControlIdentity(this, "RuntimeDialog", "Choose runtime");
        MainWindow.SetControlIdentity(_wasdk, "RuntimeWasdk", "Windows App SDK");
        MainWindow.SetControlIdentity(_winui, "RuntimeWinui", "WinUI");
        MainWindow.SetControlIdentity(_search, "RuntimeSearch", "Search runtime versions");
        MainWindow.SetControlIdentity(_prerelease, "RuntimePrerelease", "Include prerelease versions");
        MainWindow.SetControlIdentity(_versions, "RuntimeVersions", "Available runtime versions");
        MainWindow.SetControlIdentity(_browse, "RuntimeBrowse", "Browse for a local WinUI NuGet package");
        MainWindow.SetControlIdentity(_sdkMode, "RuntimeSdkMode", "SDK / API selection mode");
        MainWindow.SetControlIdentity(_sdkSearch, "RuntimeSdkSearch", "Search or enter SDK / API version");
        MainWindow.SetControlIdentity(_statusScroll, "RuntimeStatusScroll", "Runtime status details");
        _sdkMode.Items.Add("Match SDK/API to native runtime");
        _sdkMode.Items.Add("Choose Windows App SDK API");
        _sdkMode.Items.Add("Bundled API (base)");
        string sdk = SdkSelection.Normalize(snippet.Sdk);
        _sdkMode.SelectedIndex = sdk == "match" ? 0 : sdk == "base" ? 2 : 1;
        _sdkSearch.Text = sdk is "match" or "base" ? "" : sdk;
        _sdkSearch.Visibility = _sdkMode.SelectedIndex == 1 ? Visibility.Visible : Visibility.Collapsed;
        _prerelease.IsChecked = snippet.WasdkVersion?.Contains('-') == true
            || (snippet.WinUiToken?.EndsWith(".nupkg", StringComparison.OrdinalIgnoreCase) != true && snippet.WinUiToken?.Contains('-') == true)
            || sdk.Contains('-');
        AutomationProperties.SetAutomationId(_local, "RuntimeLocalPath");
        AutomationProperties.SetAutomationId(_status, "RuntimeStatus");
        AutomationProperties.SetLiveSetting(_status, AutomationLiveSetting.Polite);
        _panel.Children.Add(new TextBlock { Text = "Native: " + DescribeSelection(snippet) + "\nAPI: " + LoadedSdk.Describe(snippet), TextWrapping = TextWrapping.Wrap });
        _panel.Children.Add(new TextBlock
        {
            Text = "Apply saves one native wasdk/winui header and the API choice in the original file, then restarts. Startup runtime and SDK flags are retired for this session.",
            TextWrapping = TextWrapping.Wrap,
        });
        if (snippet.WasdkVersion is not null && snippet.WinUiToken is not null)
            _panel.Children.Add(new TextBlock
            {
                Text = "This run has an advanced mixed CLI configuration. The dialog cannot apply both; choosing either removes the other.",
                TextWrapping = TextWrapping.Wrap,
            });
        var choices = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 16 };
        choices.Children.Add(_wasdk);
        choices.Children.Add(_winui);
        _panel.Children.Add(choices);
        _panel.Children.Add(_search);
        _panel.Children.Add(_prerelease);
        _panel.Children.Add(_versions);
        _panel.Children.Add(_browse);
        _panel.Children.Add(_local);
        _panel.Children.Add(_sdkMode);
        _panel.Children.Add(_sdkSearch);
        _scroll.Content = _panel;
        _body.Content = _scroll;
        // Lock only the form during Apply/Browse. Progress stays in view, and long
        // errors remain independently scrollable without re-enabling selections.
        _statusScroll.Content = _status;
        _content.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        _content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _content.Children.Add(_body);
        Grid.SetRow(_statusScroll, 1);
        _content.Children.Add(_statusScroll);
        Content = _content;
        _wasdk.IsChecked = snippet.WasdkVersion is not null || snippet.WinUiToken is null;
        _winui.IsChecked = _wasdk.IsChecked != true;
        if (_winui.IsChecked == true && snippet.WinUiToken?.EndsWith(".nupkg", StringComparison.OrdinalIgnoreCase) == true)
        {
            _localPath = snippet.WinUiToken;
            _local.Text = "Local package: " + _localPath;
        }
        _browse.Visibility = Kind == "winui" ? Visibility.Visible : Visibility.Collapsed;
        _wasdk.Checked += ModeChanged;
        _winui.Checked += ModeChanged;
        _prerelease.Click += PrereleaseChanged;
        _search.TextChanged += SearchChanged;
        _versions.SelectionChanged += VersionChanged;
        _browse.Click += BrowseClickedAsync;
        _sdkMode.SelectionChanged += SdkModeChanged;
        _sdkSearch.TextChanged += SdkSearchChanged;
        _sdkSearch.SuggestionChosen += SdkSuggestionChosen;
        Opened += DialogOpened;
        SizeChanged += DialogSizeChanged;
        Closing += DialogClosing;
        Closed += DialogClosed;
        PrimaryButtonClick += RestartClickedAsync;
    }

    private string Kind => _winui.IsChecked == true ? "winui" : "wasdk";

    internal static string DescribeSelection(Snippet snippet, bool compact = false)
    {
        string? winui = snippet.WinUiToken;
        if (compact && winui?.EndsWith(".nupkg", StringComparison.OrdinalIgnoreCase) == true)
            winui = "local package";
        if (snippet.WasdkVersion is not null && winui is not null)
            return compact ? "Mixed (WASDK + WinUI)" : "Windows App SDK " + snippet.WasdkVersion + " + WinUI " + winui;
        return winui is not null ? "WinUI " + winui : "Windows App SDK " + (snippet.WasdkVersion ?? "unspecified");
    }

    private void DialogOpened(ContentDialog sender, ContentDialogOpenedEventArgs args)
    {
        UpdateContentHeight();
        _ = LoadVersionsAsync(selectCurrent: true);
        if (_sdkMode.SelectedIndex == 1) _ = LoadSdkVersionsAsync();
    }

    private void DialogSizeChanged(object sender, SizeChangedEventArgs args) => UpdateContentHeight();

    private void UpdateContentHeight()
    {
        if (XamlRoot is { } root)
            _content.MaxHeight = Math.Max(240, root.Size.Height - 180);
    }

    private string SdkChoice => _sdkMode.SelectedIndex switch
    {
        0 => "match",
        2 => "base",
        _ => _sdkSearch.Text.Trim(),
    };

    private void SdkModeChanged(object sender, SelectionChangedEventArgs args)
    {
        _sdkSearch.Visibility = _sdkMode.SelectedIndex == 1 ? Visibility.Visible : Visibility.Collapsed;
        if (_sdkMode.SelectedIndex == 1) _ = LoadSdkVersionsAsync();
        else _sdkLookup?.Cancel();
        UpdatePrimary();
    }

    private async Task LoadSdkVersionsAsync()
    {
        _sdkLookup?.Cancel();
        using var lookup = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _sdkLookup = lookup;
        try
        {
            var response = await _client.SendAsync(_snippet, "list", "wasdk", _prerelease.IsChecked == true, null, lookup.Token);
            if (_isClosed || lookup.IsCancellationRequested || _sdkLookup != lookup) return;
            _sdkVersions = response.Versions;
            FilterSdkVersions();
        }
        catch (OperationCanceledException) when (lookup.IsCancellationRequested) { }
        catch (Exception ex) when (MainWindow.IsToolbarFailure(ex))
        {
            if (!_isClosed && !lookup.IsCancellationRequested)
                ReportError("SDK list unavailable; an exact SDK version can still be entered. " + ex.Message);
        }
        finally { if (_sdkLookup == lookup) _sdkLookup = null; }
    }

    private void FilterSdkVersions() => _sdkSearch.ItemsSource = _sdkVersions
        .Where(v => v.Contains(_sdkSearch.Text.Trim(), StringComparison.OrdinalIgnoreCase)).Take(30).ToArray();

    private void SdkSearchChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (args.Reason == AutoSuggestionBoxTextChangeReason.UserInput) FilterSdkVersions();
        UpdatePrimary();
    }

    private void SdkSuggestionChosen(AutoSuggestBox sender, AutoSuggestBoxSuggestionChosenEventArgs args) =>
        sender.Text = (string)args.SelectedItem;

    private void ModeChanged(object sender, RoutedEventArgs args)
    {
        _localPath = null;
        _local.Text = string.Empty;
        _browse.Visibility = Kind == "winui" ? Visibility.Visible : Visibility.Collapsed;
        _ = LoadVersionsAsync();
    }

    private void PrereleaseChanged(object sender, RoutedEventArgs args)
    {
        _ = LoadVersionsAsync();
        if (_sdkMode.SelectedIndex == 1) _ = LoadSdkVersionsAsync();
    }

    private async Task LoadVersionsAsync(bool selectCurrent = false)
    {
        _lookup?.Cancel();
        var lookup = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _lookup = lookup;
        _allVersions = [];
        _versions.ItemsSource = _allVersions;
        SetStatus("Loading versions from the repro's NuGet sources...");
        UpdatePrimary();
        string kind = Kind;
        try
        {
            RunnerControlResponse response = await _client.SendAsync(
                _snippet, "list", kind, _prerelease.IsChecked == true, null, lookup.Token);
            if (_isClosed || lookup.IsCancellationRequested || _lookup != lookup) return;
            _allVersions = response.Versions;
            FilterVersions();
            // Browsing can finish before the initial feed lookup. Keep that newer choice.
            if (selectCurrent && _localPath is null)
            {
                string? current = kind == "wasdk" ? _snippet.WasdkVersion : _snippet.WinUiToken;
                if (current is not null && _allVersions.Contains(current)) _versions.SelectedItem = current;
            }
        }
        catch (OperationCanceledException) when (lookup.IsCancellationRequested) { }
        catch (Exception ex) when (MainWindow.IsToolbarFailure(ex))
        {
            if (!_isClosed && !lookup.IsCancellationRequested) ReportError(ex.Message);
        }
        finally
        {
            if (_lookup == lookup) _lookup = null;
            lookup.Dispose();
        }
    }

    private void SearchChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args) => FilterVersions();

    private void FilterVersions()
    {
        string? selected = _versions.SelectedItem as string;
        string[] filtered = _allVersions.Where(version =>
            version.Contains(_search.Text.Trim(), StringComparison.OrdinalIgnoreCase)).ToArray();
        _versions.ItemsSource = filtered;
        if (selected is not null && filtered.Contains(selected)) _versions.SelectedItem = selected;
        SetStatus(filtered.Length == 0 ? "No matching versions. Change the search or include prereleases."
            : filtered.Length + " versions. Select one, then Apply and restart.");
        UpdatePrimary();
    }

    private void VersionChanged(object sender, SelectionChangedEventArgs args)
    {
        if (_versions.SelectedItem is not null)
        {
            _localPath = null;
            _local.Text = string.Empty;
        }
        UpdatePrimary();
    }

    private async void BrowseClickedAsync(object sender, RoutedEventArgs args)
    {
        if (_isClosed || _isBrowsing) return;
        _isBrowsing = true;
        _body.IsEnabled = false;
        UpdatePrimary();
        try
        {
            var picker = new FileOpenPicker();
            picker.FileTypeFilter.Add(".nupkg");
            WinRT.Interop.InitializeWithWindow.Initialize(picker, _hwnd);
            var file = await picker.PickSingleFileAsync().AsTask(_lifetime.Token);
            if (_isClosed || file is null) return;
            _versions.SelectedItem = null;
            _localPath = file.Path;
            _local.Text = "Local package (replaces a published version): " + file.Path;
            SetStatus("The host will use the existing content-keyed package cache.");
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception ex) when (MainWindow.IsToolbarFailure(ex))
        {
            if (!_isClosed) ReportError("Could not browse for a package: " + ex.Message);
        }
        finally
        {
            _isBrowsing = false;
            if (!_isClosed)
            {
                _body.IsEnabled = true;
                UpdatePrimary();
            }
        }
    }

    private void UpdatePrimary()
    {
        bool validSdk = _sdkMode.SelectedIndex != 1 || !string.IsNullOrWhiteSpace(_sdkSearch.Text);
        try { _ = SdkSelection.Normalize(SdkChoice); }
        catch (ArgumentException) { validSdk = false; }
        IsPrimaryButtonEnabled = !_isApplying && !_isBrowsing && validSdk
            && (_versions.SelectedItem is string || (Kind == "winui" && !string.IsNullOrEmpty(_localPath)));
    }

    private async void RestartClickedAsync(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        args.Cancel = true;
        if (!IsPrimaryButtonEnabled) return;
        var deferral = args.GetDeferral();
        _lookup?.Cancel();
        _sdkLookup?.Cancel();
        _isApplying = true;
        _body.IsEnabled = false;
        CloseButtonText = string.Empty;
        UpdatePrimary();
        SetStatus("Preparing runtime. The current preview stays running until preparation and source validation succeed...");
        try
        {
            string value = _localPath ?? (string)_versions.SelectedItem;
            RunnerControlResponse result = await _client.SendAsync(
                _snippet, "apply", Kind, false, value, _lifetime.Token, SdkChoice);
            if (!_isClosed && result.IsRestarting) SetStatus("Restarting Runner...");
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception ex) when (MainWindow.IsToolbarFailure(ex))
        {
            if (!_isClosed) ReportError(ex.Message);
        }
        finally
        {
            _isApplying = false;
            if (!_isClosed)
            {
                _body.IsEnabled = true;
                CloseButtonText = "Cancel";
                UpdatePrimary();
            }
            deferral.Complete();
        }
    }

    private void SetStatus(string message)
    {
        if (_isClosed || _status.Text == message) return;
        _status.Text = message;
        _statusScroll.ChangeView(null, 0, null, disableAnimation: true);
        var peer = FrameworkElementAutomationPeer.FromElement(_status)
            ?? FrameworkElementAutomationPeer.CreatePeerForElement(_status);
        peer?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
    }

    private void ReportError(string message)
    {
        SetStatus(message);
        _reportError(message);
    }

    private void DialogClosing(ContentDialog sender, ContentDialogClosingEventArgs args)
    {
        if ((_isApplying || _isBrowsing) && !_isClosed) args.Cancel = true;
    }

    private void DialogClosed(ContentDialog sender, ContentDialogClosedEventArgs args) => Dispose();

    public void Dispose()
    {
        if (_isClosed) return;
        _isClosed = true;
        _lifetime.Cancel();
        _lookup?.Cancel();
        _sdkLookup?.Cancel();
        _lifetime.Dispose();
        _wasdk.Checked -= ModeChanged;
        _winui.Checked -= ModeChanged;
        _prerelease.Click -= PrereleaseChanged;
        _search.TextChanged -= SearchChanged;
        _versions.SelectionChanged -= VersionChanged;
        _browse.Click -= BrowseClickedAsync;
        _sdkMode.SelectionChanged -= SdkModeChanged;
        _sdkSearch.TextChanged -= SdkSearchChanged;
        _sdkSearch.SuggestionChosen -= SdkSuggestionChosen;
        Opened -= DialogOpened;
        SizeChanged -= DialogSizeChanged;
        Closing -= DialogClosing;
        Closed -= DialogClosed;
        PrimaryButtonClick -= RestartClickedAsync;
    }
}
