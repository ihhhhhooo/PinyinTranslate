using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;

namespace PinyinTranslate.App;

/// <summary>Physical screen coordinates; right and bottom are exclusive.</summary>
public readonly record struct ScreenRectangle(int Left, int Top, int Width, int Height)
{
    /// <summary>Clamp a drag to virtual-screen bounds, including negative monitor coordinates.</summary>
    public static ScreenRectangle? FromDrag(Point start, Point end, ScreenRectangle bounds)
    {
        if (bounds.Width <= 0 || bounds.Height <= 0 || !double.IsFinite(start.X) || !double.IsFinite(start.Y) || !double.IsFinite(end.X) || !double.IsFinite(end.Y)) return null;
        double rightBound = (double)bounds.Left + bounds.Width, bottomBound = (double)bounds.Top + bounds.Height;
        double l = Math.Clamp(Math.Floor(Math.Min(start.X, end.X)), bounds.Left, rightBound);
        double r = Math.Clamp(Math.Ceiling(Math.Max(start.X, end.X)), bounds.Left, rightBound);
        double t = Math.Clamp(Math.Floor(Math.Min(start.Y, end.Y)), bounds.Top, bottomBound);
        double b = Math.Clamp(Math.Ceiling(Math.Max(start.Y, end.Y)), bounds.Top, bottomBound);
        if (r - l < 2 || b - t < 2 || l < int.MinValue || r > int.MaxValue || t < int.MinValue || b > int.MaxValue || r - l > int.MaxValue || b - t > int.MaxValue) return null;
        return new((int)l, (int)t, (int)(r - l), (int)(b - t));
    }
}

public sealed class SelectionOverlay : Window
{
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetWindowPos(IntPtr window, IntPtr insertAfter, int x, int y, int width, int height, uint flags);
    private readonly Canvas _canvas = new();
    private readonly Rectangle _outline = new() { Stroke = FindBrush("OverlayOutline", Brushes.White), StrokeThickness = 2, Fill = new SolidColorBrush(Color.FromArgb(28, 255, 255, 255)) };
    private readonly ScreenRectangle _screen;
    private Point? _start;
    private bool _activated;
    private bool _finishing;
    public ScreenRectangle? Selection { get; private set; }

    public SelectionOverlay()
    {
        // With the application manifest's PerMonitorV2 context, these are physical pixels.
        _screen = new(GetSystemMetrics(76), GetSystemMetrics(77), GetSystemMetrics(78), GetSystemMetrics(79));
        if (_screen.Width <= 0 || _screen.Height <= 0) throw new InvalidOperationException("无法取得屏幕边界。");
        Title = "PinyinTranslate Screenshot Selection";
        WindowStyle = WindowStyle.None; AllowsTransparency = true; Background = new SolidColorBrush(Color.FromArgb(70, 0, 0, 0));
        ShowInTaskbar = true; Topmost = true; ResizeMode = ResizeMode.NoResize; Cursor = Cursors.Cross;
        Left = SystemParameters.VirtualScreenLeft; Top = SystemParameters.VirtualScreenTop;
        Width = SystemParameters.VirtualScreenWidth; Height = SystemParameters.VirtualScreenHeight;
        Content = _canvas; _canvas.Children.Add(_outline); _outline.Visibility = Visibility.Collapsed;
        var hint = new Border
        {
            Background = FindBrush("OverlayHintBackground", Brushes.Black),
            BorderBrush = FindBrush("CardBorder", Brushes.Transparent),
            BorderThickness = FindValue("ControlBorderThickness", new Thickness(1)),
            CornerRadius = FindValue("ControlCornerRadius", new CornerRadius(8)),
            Padding = new Thickness(16, 10, 16, 10),
            Child = new TextBlock { Text = "拖动选择截图区域  ·  按 Esc 取消", Foreground = FindBrush("OverlayHintInk", Brushes.White), FontSize = 14 }
        };
        Canvas.SetLeft(hint, 24); Canvas.SetTop(hint, 24); _canvas.Children.Add(hint);
        MouseLeftButtonDown += Down; MouseMove += Move; MouseLeftButtonUp += Up;
        KeyDown += (_, e) => { if (e.Key == Key.Escape) { Finish(false); e.Handled = true; } };
        SourceInitialized += (_, _) =>
        {
            // WPF virtual-screen properties are DIP; SetWindowPos sizes the HWND in physical pixels.
            var handle = new WindowInteropHelper(this).Handle;
            if (!SetWindowPos(handle, IntPtr.Zero, _screen.Left, _screen.Top, _screen.Width, _screen.Height, 0x0010 | 0x0004))
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "无法放置截图遮罩。");
        };
        Activated += (_, _) => _activated = true;
        Deactivated += (_, _) => { if (_activated && !_finishing && IsVisible) Finish(false); };
        Loaded += (_, _) => { Activate(); Focus(); };
    }
    private static Brush FindBrush(string name, Brush fallback) => Application.Current?.TryFindResource(name) as Brush ?? fallback;
    private static T FindValue<T>(string name, T fallback) => Application.Current?.TryFindResource(name) is T value ? value : fallback;
    private void Finish(bool accepted) { _finishing = true; DialogResult = accepted; }
    private void Down(object sender, MouseButtonEventArgs e) { _start = e.GetPosition(_canvas); _canvas.CaptureMouse(); _outline.Visibility = Visibility.Visible; Update(e.GetPosition(_canvas)); }
    private void Move(object sender, MouseEventArgs e) { if (_start.HasValue) Update(e.GetPosition(_canvas)); }
    private void Update(Point end)
    {
        if (!_start.HasValue) return;
        var p = _start.Value; Canvas.SetLeft(_outline, Math.Min(p.X, end.X)); Canvas.SetTop(_outline, Math.Min(p.Y, end.Y));
        _outline.Width = Math.Abs(end.X - p.X); _outline.Height = Math.Abs(end.Y - p.Y);
    }
    private void Up(object sender, MouseButtonEventArgs e)
    {
        if (!_start.HasValue) return;
        Point p = _canvas.PointToScreen(_start.Value), q = _canvas.PointToScreen(e.GetPosition(_canvas));
        _canvas.ReleaseMouseCapture();
        Selection = ScreenRectangle.FromDrag(p, q, _screen);
        if (Selection is null) { _start = null; _outline.Visibility = Visibility.Collapsed; return; }
        Finish(true);
    }
}
