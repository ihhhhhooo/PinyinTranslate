using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using PinyinTranslate.Input;

namespace PinyinTranslate.App;

internal sealed class ImePreviewWindow : Window
{
    private readonly TextBlock _translation = new() { FontSize = 17, TextWrapping = TextWrapping.Wrap, MaxWidth = 470 };
    private readonly TextBlock _hint = new() { FontSize = 11, Margin = new Thickness(0, 5, 0, 0), TextWrapping = TextWrapping.Wrap, MaxWidth = 470 };
    private CandidateBounds _bounds;
    private bool _positioning;
    public ImePreviewWindow()
    {
        Title = "微软拼音翻译预览";
        WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false; ShowActivated = false; Focusable = false; Topmost = true;
        SizeToContent = SizeToContent.WidthAndHeight; MinWidth = 180; MaxWidth = 510;
        var panel = new StackPanel(); panel.Children.Add(_translation); panel.Children.Add(_hint);
        var border = new Border { Padding = new Thickness(13, 9, 13, 9), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6), Child = panel };
        border.SetResourceReference(Border.BackgroundProperty, "Surface"); border.SetResourceReference(Border.BorderBrushProperty, "Accent");
        _translation.SetResourceReference(TextBlock.ForegroundProperty, "Ink"); _hint.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        Content = border;
        SourceInitialized += (_, _) =>
        {
            var handle = new WindowInteropHelper(this).Handle;
            SetWindowLongPtr(handle, -20, GetWindowLongPtr(handle, -20) | 0x08000080); // NOACTIVATE | TOOLWINDOW
            HwndSource.FromHwnd(handle)?.AddHook((nint hwnd, int msg, nint wp, nint lp, ref bool handled) =>
            {
                if (msg == 0x21) { handled = true; return 3; } // MA_NOACTIVATE
                if (msg == 0x02E0) Dispatcher.BeginInvoke(Position); // Reposition after WPF applies the new DPI.
                return 0;
            });
        };
        SizeChanged += (_, _) => Position();
    }
    public void Present(CandidateTranslationState state, string? copyHint = null)
    {
        if (state.Observation is not { } candidate) { Hide(); return; }
        _bounds = candidate.Bounds;
        _translation.Text = state.Translation ?? (state.Error is null ? "正在翻译…" : "翻译失败");
        _hint.Text = !string.IsNullOrWhiteSpace(copyHint) ? copyHint :
            state.Error is not null ? "翻译失败，请查看主窗口提示" : state.Translation is null
                ? "正在翻译，输入变化会取消本轮请求" : "正在复制译文…";
        if (!IsVisible) Show();
        UpdateLayout(); Position();
    }
    private void Position()
    {
        if (!IsVisible || _positioning || _bounds.Width <= 0) return;
        _positioning = true;
        try
        {
            var handle = new WindowInteropHelper(this).Handle;
            // Candidate bounds and GetWindowRect are physical pixels. Let WPF size
            // its own window; the target application's DPI awareness can differ.
            if (!GetWindowRect(handle, out var own)) return;
            int width = Math.Max(1, own.Right - own.Left);
            int height = Math.Max(1, own.Bottom - own.Top);
            var rect = new NativeRect { Left = _bounds.X, Top = _bounds.Y, Right = _bounds.X + _bounds.Width, Bottom = _bounds.Y + _bounds.Height };
            var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
            var monitor = MonitorFromRect(ref rect, 2);
            int x = _bounds.X, y = _bounds.Y - height - 4;
            if (GetMonitorInfo(monitor, ref info))
            {
                if (y < info.Work.Top) y = _bounds.Y + _bounds.Height + 4;
                x = Math.Clamp(x, info.Work.Left, Math.Max(info.Work.Left, info.Work.Right - width));
                y = Math.Clamp(y, info.Work.Top, Math.Max(info.Work.Top, info.Work.Bottom - height));
            }
            SetWindowPos(handle, new nint(-1), x, y, 0, 0, 0x0011); // NOSIZE | NOACTIVATE
        }
        finally { _positioning = false; }
    }
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct MonitorInfo { public int Size; public NativeRect Monitor, Work; public uint Flags; }
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern nint GetWindowLongPtr(nint hwnd, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern nint SetWindowLongPtr(nint hwnd, int index, nint value);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint hwnd, out NativeRect rect);
    [DllImport("user32.dll")] private static extern nint MonitorFromRect(ref NativeRect rect, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo info);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(nint hwnd, nint after, int x, int y, int width, int height, uint flags);
}
