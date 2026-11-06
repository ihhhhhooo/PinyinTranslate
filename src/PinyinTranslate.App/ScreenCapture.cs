using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;

namespace PinyinTranslate.App;

public static class ScreenCapture
{
    [DllImport("dwmapi.dll")] private static extern int DwmFlush();
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr GetDC(IntPtr hWnd);
    [DllImport("user32.dll", SetLastError = true)] private static extern int ReleaseDC(IntPtr hWnd, IntPtr dc);
    [DllImport("gdi32.dll", SetLastError = true)] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll", SetLastError = true)] private static extern IntPtr CreateCompatibleBitmap(IntPtr dc, int width, int height);
    [DllImport("gdi32.dll", SetLastError = true)] private static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
    [DllImport("gdi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool BitBlt(IntPtr dest, int x, int y, int width, int height, IntPtr source, int sx, int sy, uint operation);
    [DllImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DeleteDC(IntPtr dc);
    public static byte[] CapturePng(ScreenRectangle rect)
    {
        if (rect.Width <= 0 || rect.Height <= 0 || (long)rect.Width * rect.Height > 100_000_000) throw new ArgumentOutOfRangeException(nameof(rect));
        // Wait for the desktop compositor after hiding the selection overlay and app windows.
        Marshal.ThrowExceptionForHR(DwmFlush());
        IntPtr screen = GetDC(IntPtr.Zero), memory = IntPtr.Zero, bitmap = IntPtr.Zero, old = IntPtr.Zero;
        if (screen == IntPtr.Zero) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            memory = CreateCompatibleDC(screen); if (memory == IntPtr.Zero) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            bitmap = CreateCompatibleBitmap(screen, rect.Width, rect.Height); if (bitmap == IntPtr.Zero) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            old = SelectObject(memory, bitmap); if (old == IntPtr.Zero || old == new IntPtr(-1)) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            if (!BitBlt(memory, 0, 0, rect.Width, rect.Height, screen, rect.Left, rect.Top, 0x00CC0020 | 0x40000000)) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            var source = Imaging.CreateBitmapSourceFromHBitmap(bitmap, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(source));
            using var stream = new MemoryStream(); encoder.Save(stream); return stream.ToArray();
        }
        finally { if (old != IntPtr.Zero && old != new IntPtr(-1)) SelectObject(memory, old); if (bitmap != IntPtr.Zero) DeleteObject(bitmap); if (memory != IntPtr.Zero) DeleteDC(memory); ReleaseDC(IntPtr.Zero, screen); }
    }
}
