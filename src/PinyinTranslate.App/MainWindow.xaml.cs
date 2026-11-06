using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using PinyinTranslate.Core;

namespace PinyinTranslate.App;

public partial class MainWindow : Window
{
    private readonly IAppServices _services;
    private bool _refreshing;
    private bool _capturing;
    private bool _savingSettings;
    private string? _languageSettingsSignature;
    private string _visualTheme = "light";
    private AppearancePage? _settingsPage;
    private StatisticsPage? _statisticsPage;
    private readonly CancellationTokenSource _shutdown = new();

    public MainWindow(IAppServices services)
    {
        _services = services;
        ThemeManager.Apply(_services.Settings.Theme);
        _visualTheme = _services.Settings.Theme;
        InitializeComponent();
        MainContent.SizeChanged += (_, _) => UpdateHomeLayout();
        UpdateHomeLayout();
        ShowPage("home");
        _services.StateChanged += ServicesOnStateChanged;
        _services.ScreenshotRequested += ServicesOnScreenshotRequested;
        var settings = services.Settings;
        ImeShortcut.Text = settings.ImeHotkey;
        SelectionShortcut.Text = settings.SelectionHotkey;
        ScreenshotShortcut.Text = settings.ScreenshotHotkey;
        ImeTargetChoice.ItemsSource = TranslationLanguages.All;
        SelectionLanguageAChoice.ItemsSource = TranslationLanguages.All;
        SelectionLanguageBChoice.ItemsSource = TranslationLanguages.All;
        ScreenshotTargetChoice.ItemsSource = TranslationLanguages.All;
        RefreshState();
        Closed += (_, _) => { _shutdown.Cancel(); _services.StateChanged -= ServicesOnStateChanged; _services.ScreenshotRequested -= ServicesOnScreenshotRequested; _statisticsPage?.Dispose(); _settingsPage?.Dispose(); _services.Dispose(); _shutdown.Dispose(); };
    }

    private void ServicesOnStateChanged(object? sender, EventArgs e) => Dispatcher.BeginInvoke(RefreshState);
    private void ServicesOnScreenshotRequested(object? sender, EventArgs e) => Dispatcher.BeginInvoke(async () => await CaptureAsync());
    private void RefreshState()
    {
        _refreshing = true;
        try
        {
            SelectionCheck.IsChecked = _services.SelectionEnabled;
            ImeCheck.IsChecked = _services.ImeEnabled;
            StatusText.Text = _services.Status;
            ImeShortcut.IsReadOnly = _savingSettings || _services.IsPreparingForUpdate;
            SelectionShortcut.IsReadOnly = _savingSettings || _services.IsPreparingForUpdate;
            ScreenshotShortcut.IsReadOnly = _savingSettings || _services.IsPreparingForUpdate;
            bool canEditLanguages = !_savingSettings && !_services.IsPreparingForUpdate;
            ImeTargetChoice.IsEnabled = SelectionLanguageAChoice.IsEnabled = SelectionLanguageBChoice.IsEnabled =
                ScreenshotTargetChoice.IsEnabled = SwapLanguagesButton.IsEnabled = SaveFeatureSettingsButton.IsEnabled = canEditLanguages;
            var settings = _services.Settings;
            string signature = string.Join('|', settings.ImeTargetLanguage, settings.SelectionLanguageA, settings.SelectionLanguageB, settings.ScreenshotTargetLanguage);
            // Status refreshes must not erase unsaved dropdown choices.
            if (_languageSettingsSignature != signature)
            {
                ImeTargetChoice.SelectedValue = settings.ImeTargetLanguage;
                SelectionLanguageAChoice.SelectedValue = settings.SelectionLanguageA;
                SelectionLanguageBChoice.SelectedValue = settings.SelectionLanguageB;
                ScreenshotTargetChoice.SelectedValue = settings.ScreenshotTargetLanguage;
                _languageSettingsSignature = signature;
            }
        }
        finally { _refreshing = false; }
    }
    private void OpenSettings(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_settingsPage == null)
            {
                _settingsPage = new AppearancePage(_services, ApplyVisualTheme, CanRestartForUpdate);
                _settingsPage.BackRequested += (_, _) =>
                {
                    ShowPage("home");
                    HomeNav.Focus();
                };
                SettingsHost.Content = _settingsPage;
            }
            _settingsPage.RefreshSelection();
            _settingsPage.RefreshStartup();
            ShowPage("settings");
        }
        catch (Exception) { ShowError("无法打开设置，请稍后重试。"); }
    }

    private void OpenHome(object sender, RoutedEventArgs e) => ShowPage("home");

    private void OpenStatistics(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_statisticsPage == null)
            {
                _statisticsPage = new StatisticsPage(_services);
                StatisticsHost.Content = _statisticsPage;
            }
            ShowPage("statistics");
        }
        catch (Exception) { ShowError("无法打开统计，请稍后重试。"); }
    }

    private void ShowPage(string page)
    {
        HomeView.Visibility = page == "home" ? Visibility.Visible : Visibility.Collapsed;
        StatisticsHost.Visibility = page == "statistics" ? Visibility.Visible : Visibility.Collapsed;
        SettingsHost.Visibility = page == "settings" ? Visibility.Visible : Visibility.Collapsed;
        UpdateNavButton(HomeNav, page == "home");
        UpdateNavButton(StatisticsNav, page == "statistics");
        UpdateNavButton(SettingsButton, page == "settings");
    }

    private static void UpdateNavButton(Button button, bool selected)
    {
        button.SetResourceReference(Control.BackgroundProperty, selected ? "RowSelected" : "Surface");
        button.SetResourceReference(Control.BorderBrushProperty, selected ? "Accent" : "Surface");
    }

    private void ApplyVisualTheme(string theme)
    {
        // All open windows use dynamic resources; changing the shared palette updates them immediately.
        ThemeManager.Apply(theme);
        _visualTheme = theme;
        UpdateHomeLayout();
    }

    private void UpdateHomeLayout()
    {
        bool collage = _visualTheme == "collage";
        bool comic = _visualTheme == "comic";
        bool gold = _visualTheme == "gold";
        bool pop = _visualTheme == "pop";

        HomeLayout.MaxWidth = gold ? 760 : pop || collage ? 980 : 840;
        HomeLeft.Width = new GridLength(1, GridUnitType.Star);
        HomeGap.Width = new GridLength(0);
        HomeRight.Width = new GridLength(0);

        Grid.SetRow(FeatureCard, 2);
        Grid.SetColumn(FeatureCard, 0);
        Grid.SetColumnSpan(FeatureCard, 3);
        Grid.SetRow(HomeFooter, 3);
        FeatureCard.Margin = comic ? new Thickness(18, 0, 0, 14) : collage ? new Thickness(0, 0, 20, 14) : new Thickness(0, 0, 0, 14);

        HomeHeader.MinHeight = pop || comic || gold ? 68 : 52;
        HeaderBackdrop.Visibility = pop || comic || gold ? Visibility.Visible : Visibility.Collapsed;
        HeaderBackdrop.SetResourceReference(Border.BackgroundProperty, pop ? "HeaderBadge" : comic ? "BadgeScreenshot" : "Surface");
        HeaderRule.Visibility = gold ? Visibility.Visible : Visibility.Collapsed;
        HeaderOrnament.Visibility = _visualTheme == "light" || comic ? Visibility.Collapsed : Visibility.Visible;
        ComicRays.Visibility = comic ? Visibility.Visible : Visibility.Collapsed;
        ComicSticker.Visibility = comic ? Visibility.Visible : Visibility.Collapsed;
        HeaderOrnament.Opacity = pop ? 0.45 : gold ? 0.65 : 0.30;
        BrandIdentity.HorizontalAlignment = gold ? HorizontalAlignment.Center : HorizontalAlignment.Left;
        BrandIdentity.Margin = new Thickness(pop || comic ? 12 : 0, 0, 0, 0);
        BrandTitle.FontWeight = pop || comic ? FontWeights.Black : gold ? FontWeights.SemiBold : FontWeights.Bold;
        ThemeStamp.Visibility = pop || comic || collage ? Visibility.Visible : Visibility.Collapsed;
        ThemeStampText.Text = pop ? "POP!" : comic ? "灵感!" : "纸上译语";
    }
    private async void SelectionChanged(object sender, RoutedEventArgs e)
    {
        if (_refreshing || !IsInitialized) return;
        try { var error = await _services.SetSelectionEnabledAsync(SelectionCheck.IsChecked == true); if (error != null) ShowError(error); }
        catch (Exception ex) { ShowError(ex.Message); }
        RefreshState();
    }
    private async void ImeChanged(object sender, RoutedEventArgs e)
    {
        if (_refreshing || !IsInitialized) return;
        try { var error = await _services.SetImeEnabledAsync(ImeCheck.IsChecked == true); if (error != null) ShowError(error); }
        catch (Exception ex) { ShowError(ex.Message); }
        RefreshState();
    }
    private async void SaveSettingsClicked(object sender, RoutedEventArgs e)
    {
        if (_savingSettings || _services.IsPreparingForUpdate) return;
        _savingSettings = true;
        RefreshState();
        try
        {
            var settings = _services.Settings with
            {
                ImeHotkey = ImeShortcut.Text.Trim(), SelectionHotkey = SelectionShortcut.Text.Trim(), ScreenshotHotkey = ScreenshotShortcut.Text.Trim(),
                ImeTargetLanguage = ImeTargetChoice.SelectedValue as string ?? "",
                SelectionLanguageA = SelectionLanguageAChoice.SelectedValue as string ?? "",
                SelectionLanguageB = SelectionLanguageBChoice.SelectedValue as string ?? "",
                ScreenshotTargetLanguage = ScreenshotTargetChoice.SelectedValue as string ?? ""
            };
            var error = await _services.ApplySettingsAsync(settings);
            ShortcutStatus.Text = error ?? "快捷键与翻译语言已保存";
            ShortcutStatus.SetResourceReference(TextBlock.ForegroundProperty, error == null ? "Muted" : "ErrorInk");
        }
        catch (Exception) { ShortcutStatus.Text = "保存失败，请检查快捷键和语言后重试"; ShortcutStatus.SetResourceReference(TextBlock.ForegroundProperty, "ErrorInk"); }
        finally { _savingSettings = false; RefreshState(); }
    }

    private void SettingsEdited(object sender, RoutedEventArgs e)
    {
        if (_refreshing || _languageSettingsSignature is null) return;
        ShortcutStatus.Text = "修改快捷键或语言后点击保存";
        ShortcutStatus.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
    }

    private void SwapLanguagesClicked(object sender, RoutedEventArgs e)
    {
        if (_savingSettings || _services.IsPreparingForUpdate) return;
        var a = SelectionLanguageAChoice.SelectedValue;
        SelectionLanguageAChoice.SelectedValue = SelectionLanguageBChoice.SelectedValue;
        SelectionLanguageBChoice.SelectedValue = a;
    }

    private bool CanRestartForUpdate() => !_capturing && !_savingSettings &&
        ImeShortcut.Text.Trim() == _services.Settings.ImeHotkey &&
        SelectionShortcut.Text.Trim() == _services.Settings.SelectionHotkey &&
        ScreenshotShortcut.Text.Trim() == _services.Settings.ScreenshotHotkey &&
        ImeTargetChoice.SelectedValue as string == _services.Settings.ImeTargetLanguage &&
        SelectionLanguageAChoice.SelectedValue as string == _services.Settings.SelectionLanguageA &&
        SelectionLanguageBChoice.SelectedValue as string == _services.Settings.SelectionLanguageB &&
        ScreenshotTargetChoice.SelectedValue as string == _services.Settings.ScreenshotTargetLanguage;
    private async void CaptureClicked(object sender, RoutedEventArgs e) => await CaptureAsync();
    private void ShowError(string message) => MessageBox.Show(this, message, "PinyinTranslate", MessageBoxButton.OK, MessageBoxImage.Warning);

    public async Task CaptureAsync()
    {
        if (_capturing || _services.IsPreparingForUpdate) return;
        _capturing = true;
        var hidden = new List<Window>();
        IDisposable? suppression = null;
        try
        {
            suppression = _services.SuspendSelectionForCapture();
            hidden = Application.Current.Windows.Cast<Window>().Where(w => w.IsVisible).ToList();
            foreach (var w in hidden) w.Hide();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            await Task.Delay(150, _shutdown.Token);
            if (_services.IsPreparingForUpdate) return;
            var overlay = new SelectionOverlay();
            var accepted = overlay.ShowDialog();
            if (accepted != true || overlay.Selection is null) return;
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            await Task.Delay(150, _shutdown.Token);
            byte[] png = ScreenCapture.CapturePng(overlay.Selection.Value);
            RestoreWindows(hidden);
            hidden.Clear();
            suppression.Dispose(); suppression = null;
            var resultWindow = new ScreenshotResultWindow(png, _services.RecordCopy) { Owner = this };
            resultWindow.Show();
            using var request = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
            EventHandler onClosed = (_, _) => request.Cancel();
            resultWindow.Closed += onClosed;
            try
            {
                var result = await _services.TranslateScreenshotAsync(png, request.Token);
                if (!request.IsCancellationRequested) resultWindow.SetResult(result);
            }
            catch (OperationCanceledException) { if (resultWindow.IsVisible) resultWindow.SetError("请求已取消。"); }
            catch (Exception ex) { if (resultWindow.IsVisible) resultWindow.SetError("截图翻译失败：" + ex.Message); }
            finally { resultWindow.Closed -= onClosed; }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { ShowError("截图失败：" + ex.Message); }
        finally
        {
            try { RestoreWindows(hidden); }
            finally { try { suppression?.Dispose(); } finally { _capturing = false; } }
        }
    }

    private static void RestoreWindows(IEnumerable<Window> windows)
    {
        // A selection popup can close while the overlay's modal loop is active.
        // Closed WPF windows cannot be shown again.
        foreach (var window in windows)
        {
            if (!Application.Current.Windows.Cast<Window>().Contains(window)) continue;
            // Restoring a window after an interrupted capture must not take focus
            // back from the application to which the user switched.
            bool previousShowActivated = window.ShowActivated;
            try { window.ShowActivated = false; window.Show(); }
            catch (InvalidOperationException) { }
            finally { window.ShowActivated = previousShowActivated; }
        }
    }
}
