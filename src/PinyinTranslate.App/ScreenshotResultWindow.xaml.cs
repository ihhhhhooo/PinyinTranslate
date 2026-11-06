using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;

namespace PinyinTranslate.App;

public partial class ScreenshotResultWindow : Window
{
    private readonly Action<string, bool>? _onCopied;
    private IReadOnlyList<ScreenshotItem> _items = Array.Empty<ScreenshotItem>();
    private Point? _dragStart;
    private double _startX, _startY;
    public ScreenshotResultWindow(byte[] png, Action<string, bool>? onCopied = null)
    {
        _onCopied = onCopied;
        InitializeComponent();
        using var stream = new MemoryStream(png, false); var image = new BitmapImage(); image.BeginInit(); image.CacheOption = BitmapCacheOption.OnLoad; image.StreamSource = stream; image.EndInit(); image.Freeze(); OriginalImage.Source = image;
    }
    public void SetResult(ScreenshotResult result)
    {
        _items = result.Items; Items.ItemsSource = _items;
        StatusLabel.Text = result.IsComplete ? "识别与翻译已返回，请对照原图核验" : "完整性待核对，请对照原图核验";
        if (!string.IsNullOrWhiteSpace(result.Warning)) StatusLabel.Text += " · " + result.Warning;
    }
    public void SetError(string error) => StatusLabel.Text = error;
    private void CopyLineClicked(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioButton { DataContext: ScreenshotItem item, Tag: string kind } line) return;
        string text = kind == "Original" ? item.Original : item.Translation;
        if (Copy(text, kind == "Original")) CopyFeedback.Text = $"已复制第 {item.Id} 组{(kind == "Original" ? "原文" : "译文")}";
        else { line.IsChecked = false; CopyFeedback.Text = "未复制，请重试"; }
    }
    private void CopyAllOriginal(object sender, RoutedEventArgs e) =>
        CopyFeedback.Text = Copy(string.Join(Environment.NewLine + Environment.NewLine, _items.Select(x => x.Original)), true) ? "已复制全部原文" : "未复制，请重试";
    private void CopyAll(object sender, RoutedEventArgs e) =>
        CopyFeedback.Text = Copy(string.Join(Environment.NewLine + Environment.NewLine, _items.Select(x => x.Translation)), false) ? "已复制全部译文" : "未复制，请重试";
    private bool Copy(string text, bool original)
    {
        if (string.IsNullOrEmpty(text)) return false;
        try
        {
            Clipboard.SetText(text);
            try { _onCopied?.Invoke(text, original); } catch (Exception) { }
            return true;
        }
        catch (Exception ex) { MessageBox.Show(this, "无法写入剪贴板：" + ex.Message, "复制失败", MessageBoxButton.OK, MessageBoxImage.Warning); return false; }
    }
    private void ImageDown(object sender, MouseButtonEventArgs e) { if (e.OriginalSource is not System.Windows.Controls.Image) return; _dragStart = e.GetPosition(ImageScroll); _startX = ImageScroll.HorizontalOffset; _startY = ImageScroll.VerticalOffset; ImageScroll.CaptureMouse(); }
    private void ImageMove(object sender, MouseEventArgs e) { if (!_dragStart.HasValue || e.LeftButton != MouseButtonState.Pressed) return; var p = e.GetPosition(ImageScroll); ImageScroll.ScrollToHorizontalOffset(_startX - (p.X - _dragStart.Value.X)); ImageScroll.ScrollToVerticalOffset(_startY - (p.Y - _dragStart.Value.Y)); }
    private void ImageUp(object sender, MouseButtonEventArgs e) { _dragStart = null; ImageScroll.ReleaseMouseCapture(); }
}
