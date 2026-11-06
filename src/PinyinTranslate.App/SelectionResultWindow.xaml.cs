using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using PinyinTranslate.Core;

namespace PinyinTranslate.App;

/// <summary>A nonactivating result panel; text is copied only when a row is clicked.</summary>
public partial class SelectionResultWindow : Window
{
    private readonly Action<string, bool>? _onCopied;
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr64(IntPtr hwnd, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern IntPtr SetWindowLongPtr64(IntPtr hwnd, int index, IntPtr value);

    public SelectionResultWindow(string source, string translation, Action<string, bool>? onCopied = null)
    {
        _onCopied = onCopied;
        InitializeComponent();
        OriginalLine.Content = source;
        TranslatedLine.Content = translation;
        TranslationLabel.Text = SelectionTranslationLanguage.ShouldTranslateToChinese(source) ? "译文 · 中文" : "译文 · 英文";
        SourceInitialized += (_, _) =>
        {
            var handle = new WindowInteropHelper(this).Handle;
            long style = GetWindowLongPtr64(handle, -20).ToInt64();
            SetWindowLongPtr64(handle, -20, new IntPtr(style | 0x08000000)); // WS_EX_NOACTIVATE
        };
        Loaded += (_, _) =>
        {
            var area = SystemParameters.WorkArea;
            Left = Math.Max(area.Left, area.Right - ActualWidth - 20);
            Top = Math.Max(area.Top, area.Bottom - ActualHeight - 20);
        };
    }

    private void CopyLineClicked(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioButton { Content: string text, Tag: string kind } line || string.IsNullOrEmpty(text)) return;
        try
        {
            Clipboard.SetText(text);
            try { _onCopied?.Invoke(text, kind == "Original"); } catch (Exception) { }
            CopyFeedback.Text = kind == "Original" ? "已复制原文" : "已复制译文";
        }
        catch (Exception)
        {
            line.IsChecked = false;
            CopyFeedback.Text = "复制失败，请重试";
        }
    }
}
