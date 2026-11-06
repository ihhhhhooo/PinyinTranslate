using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using PinyinTranslate.Update;

namespace PinyinTranslate.App;

public partial class AppearancePage : UserControl, IDisposable
{
    private readonly IAppServices _services;
    private readonly Action<string> _applyTheme;
    private readonly AppearanceChoice[] _choices;
    private readonly Func<bool> _canRestart;
    private bool _applying;
    private bool _loadingKey = true;
    private bool _loadingStartup = true;
    private bool _pointerPending;
    private bool _dragging;
    private Point _dragOrigin;
    private double _dragOffset;
    private UpdateService? _updates;
    private long _updateRevision = -1;
    private bool _disposed;
    private bool _keyDirty;
    private bool _savingKey;

    public event EventHandler? BackRequested;

    public AppearancePage(IAppServices services, Action<string> applyTheme, Func<bool>? canRestart = null)
    {
        _services = services;
        _applyTheme = applyTheme;
        _canRestart = canRestart ?? (() => true);
        _choices =
        [
            new("light", "极简风", "留白与细线，纵向布局清爽专注。"),
            new("pop", "波普风", "醒目撞色、粗描边与鲜明色块。"),
            new("comic", "漫画风", "蓝黄撞色、漫画网点和黑色描边。"),
            new("collage", "剪纸拼贴风", "纸张肌理、胶带和层叠拼贴。"),
            new("gold", "黑金鎏金风", "居中排版、深色底面和鎏金线条。")
        ];
        InitializeComponent();
        ThemeCards.ItemsSource = _choices;
        LoadSavedApiKey();
        RefreshSelection();
        try
        {
            var lifecycle = new DesktopUpdateLifecycle(services, Dispatcher,
                () => !_keyDirty && !_savingKey && !_applying && _canRestart(), () => _disposed);
            _updates = new UpdateService(new VelopackBackend(), lifecycle);
            _updates.StateChanged += UpdateStateChanged;
            RenderUpdate(_updates.State);
        }
        catch (Exception)
        {
            UpdateVersionText.Text = "当前版本 " + typeof(App).Assembly.GetName().Version?.ToString(3);
            UpdateStatusText.Text = "无法初始化更新服务，可从发布页下载安装版或完整便携包。";
        }
    }

    private void LoadSavedApiKey()
    {
        _loadingKey = true;
        try
        {
            var savedKey = _services.GetSavedApiKey();
            if (!string.IsNullOrWhiteSpace(savedKey))
            {
                KeyBox.Password = savedKey;
                SetKeyStatus("已保存 · 密钥已加密存储在本机");
            }
            else SetKeyStatus("尚未设置密钥");
        }
        catch (Exception) { SetKeyStatus("无法读取已保存的密钥，请重新输入并保存", true); }
        finally { _loadingKey = false; UpdateKeyPlaceholder(); }
    }

    private void KeyChanged(object sender, RoutedEventArgs e)
    {
        UpdateKeyPlaceholder();
        if (!_loadingKey && KeyStatus != null)
        {
            _keyDirty = true;
            SetKeyStatus(string.IsNullOrWhiteSpace(KeyBox.Password) ? "请输入密钥后保存" : "密钥已修改，请点击保存");
        }
    }

    private void UpdateKeyPlaceholder()
    {
        if (KeyPlaceholder != null && KeyBox != null)
            KeyPlaceholder.Visibility = string.IsNullOrEmpty(KeyBox.Password) ? Visibility.Visible : Visibility.Collapsed;
    }

    private void SetKeyStatus(string message, bool isError = false)
    {
        KeyStatus.Text = message;
        KeyStatus.SetResourceReference(TextBlock.ForegroundProperty, isError ? "ErrorInk" : "Muted");
    }

    private async void SaveKeyClicked(object sender, RoutedEventArgs e)
    {
        if (_savingKey || _disposed || _services.IsPreparingForUpdate) return;
        var key = KeyBox.Password;
        _savingKey = true;
        SaveKeyButton.IsEnabled = false;
        try
        {
            if (string.IsNullOrWhiteSpace(key)) { SetKeyStatus("请输入密钥后保存", true); return; }
            var error = await _services.SaveApiKeyAsync(key);
            if (_disposed) return;
            if (error != null) { SetKeyStatus(error, true); return; }
            if (KeyBox.Password != key) return;
            _loadingKey = true;
            try { KeyBox.Password = key.Trim(); }
            finally { _loadingKey = false; }
            _keyDirty = false;
            SetKeyStatus("已保存 · 密钥已加密存储在本机");
            UpdateKeyPlaceholder();
        }
        catch (Exception) { SetKeyStatus("保存失败，请检查本机存储权限后重试", true); }
        finally
        {
            _savingKey = false;
            if (!_disposed) SaveKeyButton.IsEnabled = true;
        }
    }

    private void UpdateStateChanged(UpdateState state)
    {
        if (_disposed || Dispatcher.HasShutdownStarted) return;
        Dispatcher.BeginInvoke(() => { if (!_disposed) RenderUpdate(state); });
    }

    private void RenderUpdate(UpdateState state)
    {
        if (_disposed || state.Revision <= _updateRevision) return;
        _updateRevision = state.Revision;
        UpdateVersionText.Text = "当前版本 " + state.CurrentVersion +
            (state.Release is null ? "" : "  →  " + state.Release.Version +
             (state.Release.DownloadBytes > 0 ? $"（{state.Release.DownloadBytes / 1048576d:F1} MB）" : ""));
        UpdateStatusText.Text = state.Message;
        UpdateStatusText.SetResourceReference(TextBlock.ForegroundProperty,
            state.Phase == UpdatePhase.Failed ? "ErrorInk" : "Ink");
        CheckUpdateButton.IsEnabled = state.CanCheck;
        DownloadUpdateButton.Visibility = state.CanDownload ? Visibility.Visible : Visibility.Collapsed;
        InstallUpdateButton.Visibility = state.CanInstall ? Visibility.Visible : Visibility.Collapsed;
        CancelUpdateButton.Visibility = state.CanCancel ? Visibility.Visible : Visibility.Collapsed;
        UpdateProgress.Visibility = state.Phase == UpdatePhase.Downloading ? Visibility.Visible : Visibility.Collapsed;
        UpdateProgress.Value = state.Progress;
        var notes = state.Release?.Notes ?? "";
        UpdateNotes.Text = notes.Length > 24000 ? notes[..24000] : notes;
        UpdateNotes.Visibility = string.IsNullOrWhiteSpace(notes) ? Visibility.Collapsed : Visibility.Visible;
        bool preparing = state.Phase is UpdatePhase.PreparingRestart or UpdatePhase.RestartScheduled;
        KeyBox.IsEnabled = !preparing;
        SaveKeyButton.IsEnabled = !preparing && !_savingKey;
        ThemeCards.IsEnabled = !preparing;
        StartupToggle.IsEnabled = !preparing;
    }

    private async void CheckUpdateClicked(object sender, RoutedEventArgs e)
    {
        if (_updates != null) await _updates.CheckAsync();
    }

    private async void DownloadUpdateClicked(object sender, RoutedEventArgs e)
    {
        if (_updates != null) await _updates.DownloadAsync();
    }

    private async void InstallUpdateClicked(object sender, RoutedEventArgs e)
    {
        if (_keyDirty || _savingKey || _applying)
        {
            UpdateStatusText.Text = "请先保存修改的密钥，并等待设置保存完成，再更新并重启。";
            return;
        }
        if (!_canRestart())
        {
            UpdateStatusText.Text = "请先完成截图，并保存修改过的快捷键和翻译语言，再更新并重启。";
            return;
        }
        if (_updates != null) await _updates.InstallAsync();
    }

    private void CancelUpdateClicked(object sender, RoutedEventArgs e) => _updates?.Cancel();

    private void OpenReleasesClicked(object sender, RoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo(VelopackBackend.RepositoryUrl + "/releases/latest") { UseShellExecute = true }); }
        catch (Exception) { UpdateStatusText.Text = "无法打开浏览器，请手动打开 github.com/ihhhhhooo/trans/releases。"; }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_updates != null)
        {
            _updates.StateChanged -= UpdateStateChanged;
            _updates.Dispose();
        }
        ClearSensitiveData();
    }

    public void ClearSensitiveData()
    {
        _loadingKey = true;
        try { KeyBox.Clear(); }
        finally { _loadingKey = false; }
    }

    public void RefreshStartup()
    {
        _loadingStartup = true;
        try
        {
            var state = StartupManager.Read();
            StartupToggle.IsEnabled = !_services.IsPreparingForUpdate;
            StartupToggle.IsChecked = state.Enabled;
            SetStartupStatus(!state.Enabled ? "登录当前 Windows 账户后自动启动" :
                state.PointsToCurrentProgram ? "已添加到当前账户的 Windows 启动项" :
                "启动项指向其他版本；关闭后重新开启可改为当前程序。");
        }
        catch (Exception)
        {
            StartupToggle.IsEnabled = false;
            SetStartupStatus("无法读取 Windows 启动项，请检查当前账户权限。", true);
        }
        finally { _loadingStartup = false; }
    }

    private void StartupChanged(object sender, RoutedEventArgs e)
    {
        if (_loadingStartup || !IsInitialized || _services.IsPreparingForUpdate) return;
        try
        {
            StartupManager.SetEnabled(StartupToggle.IsChecked == true);
            RefreshStartup();
        }
        catch (Exception)
        {
            RefreshStartup();
            SetStartupStatus("无法更新 Windows 启动项，请检查当前账户权限或程序路径。", true);
        }
    }

    private void SetStartupStatus(string message, bool isError = false)
    {
        StartupStatus.Text = message;
        StartupStatus.SetResourceReference(TextBlock.ForegroundProperty, isError ? "ErrorInk" : "Muted");
    }

    public void RefreshSelection()
    {
        foreach (var choice in _choices) choice.IsCurrent = choice.Id == _services.Settings.Theme;
        ThemeStatus.Text = "当前使用：" + ThemeManager.DisplayName(_services.Settings.Theme) + "。切换会立即生效。";
        ThemeStatus.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
    }

    private async void SelectThemeClicked(object sender, RoutedEventArgs e)
    {
        if (_dragging || _applying || _services.IsPreparingForUpdate || sender is not Button { Tag: string theme } || !ThemeManager.IsSupported(theme)) return;
        if (theme == _services.Settings.Theme) return;

        _applying = true;
        string previous = _services.Settings.Theme;
        try
        {
            // Apply before saving so every open window updates without a restart.
            _applyTheme(theme);
            string? error = await _services.SetThemeAsync(theme);
            if (error != null)
            {
                _applyTheme(previous);
                ThemeStatus.Text = error;
                ThemeStatus.SetResourceReference(TextBlock.ForegroundProperty, "ErrorInk");
                return;
            }
            RefreshSelection();
        }
        catch (Exception)
        {
            try { _applyTheme(_services.Settings.Theme); } catch (Exception) { }
            ThemeStatus.Text = "风格切换或保存失败，请稍后重试。";
            ThemeStatus.SetResourceReference(TextBlock.ForegroundProperty, "ErrorInk");
        }
        finally { _applying = false; }
    }

    private void BackClicked(object sender, RoutedEventArgs e) => BackRequested?.Invoke(this, EventArgs.Empty);

    private void PreviousClicked(object sender, RoutedEventArgs e) =>
        ThemeScroller.ScrollToHorizontalOffset(ThemeScroller.HorizontalOffset - 264);

    private void NextClicked(object sender, RoutedEventArgs e) =>
        ThemeScroller.ScrollToHorizontalOffset(ThemeScroller.HorizontalOffset + 264);

    private void ScrollerChanged(object sender, ScrollChangedEventArgs e)
    {
        if (PreviousButton == null || NextButton == null) return;
        PreviousButton.IsEnabled = e.HorizontalOffset > 0.5;
        NextButton.IsEnabled = e.HorizontalOffset < e.ExtentWidth - e.ViewportWidth - 0.5;
    }

    private void ScrollWheel(object sender, MouseWheelEventArgs e)
    {
        if (ThemeScroller.ScrollableWidth <= 0) return;
        ThemeScroller.ScrollToHorizontalOffset(ThemeScroller.HorizontalOffset - e.Delta);
        e.Handled = true;
    }

    private void DragStart(object sender, MouseButtonEventArgs e)
    {
        if (ThemeScroller.ScrollableWidth <= 0 || FromScrollbar(e.OriginalSource as DependencyObject)) return;
        _pointerPending = true;
        _dragging = false;
        _dragOrigin = e.GetPosition(ThemeScroller);
        _dragOffset = ThemeScroller.HorizontalOffset;
    }

    private void DragMove(object sender, MouseEventArgs e)
    {
        if (!_pointerPending || e.LeftButton != MouseButtonState.Pressed) return;
        Point position = e.GetPosition(ThemeScroller);
        double dx = position.X - _dragOrigin.X;
        double dy = position.Y - _dragOrigin.Y;
        if (!_dragging && Math.Abs(dx) >= SystemParameters.MinimumHorizontalDragDistance && Math.Abs(dx) > Math.Abs(dy))
        {
            // Taking capture cancels a card's press, so dragging does not apply it.
            _dragging = Mouse.Capture(ThemeScroller);
        }
        if (!_dragging) return;
        ThemeScroller.ScrollToHorizontalOffset(_dragOffset - dx);
        e.Handled = true;
    }

    private void DragEnd(object sender, MouseButtonEventArgs e)
    {
        bool wasDragging = _dragging;
        _pointerPending = false;
        _dragging = false;
        if (wasDragging)
        {
            if (Mouse.Captured == ThemeScroller) Mouse.Capture(null);
            e.Handled = true;
        }
    }

    private static bool FromScrollbar(DependencyObject? node)
    {
        while (node != null)
        {
            if (node is ScrollBar or Thumb) return true;
            if (node is not Visual) return false;
            node = VisualTreeHelper.GetParent(node);
        }
        return false;
    }
}

public sealed class AppearanceChoice : INotifyPropertyChanged
{
    private bool _isCurrent;
    public string Id { get; }
    public string Name { get; }
    public string Description { get; }
    public bool IsCurrent
    {
        get => _isCurrent;
        set
        {
            if (_isCurrent == value) return;
            _isCurrent = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsCurrent)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ActionLabel)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(AccessibilityName)));
        }
    }
    public string ActionLabel => IsCurrent ? "正在使用" : "点击使用";
    public string AccessibilityName => IsCurrent ? Name + "，正在使用" : "使用" + Name;
    public event PropertyChangedEventHandler? PropertyChanged;

    public AppearanceChoice(string id, string name, string description)
    {
        Id = id;
        Name = name;
        Description = description;
    }
}
