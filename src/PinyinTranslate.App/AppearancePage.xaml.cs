using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace PinyinTranslate.App;

public partial class AppearancePage : UserControl
{
    private readonly IAppServices _services;
    private readonly Action<string> _applyTheme;
    private readonly AppearanceChoice[] _choices;
    private bool _applying;
    private bool _loadingKey = true;
    private bool _loadingStartup = true;
    private bool _pointerPending;
    private bool _dragging;
    private Point _dragOrigin;
    private double _dragOffset;

    public event EventHandler? BackRequested;

    public AppearancePage(IAppServices services, Action<string> applyTheme)
    {
        _services = services;
        _applyTheme = applyTheme;
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
            SetKeyStatus(string.IsNullOrWhiteSpace(KeyBox.Password) ? "请输入密钥后保存" : "密钥已修改，请点击保存");
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
        var key = KeyBox.Password;
        try
        {
            if (string.IsNullOrWhiteSpace(key)) { SetKeyStatus("请输入密钥后保存", true); return; }
            var error = await _services.SaveApiKeyAsync(key);
            if (error != null) { SetKeyStatus(error, true); return; }
            _loadingKey = true;
            try { KeyBox.Password = key.Trim(); }
            finally { _loadingKey = false; }
            SetKeyStatus("已保存 · 密钥已加密存储在本机");
            UpdateKeyPlaceholder();
        }
        catch (Exception) { SetKeyStatus("保存失败，请检查本机存储权限后重试", true); }
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
            StartupToggle.IsEnabled = true;
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
        if (_loadingStartup || !IsInitialized) return;
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
        if (_dragging || _applying || sender is not Button { Tag: string theme } || !ThemeManager.IsSupported(theme)) return;
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
