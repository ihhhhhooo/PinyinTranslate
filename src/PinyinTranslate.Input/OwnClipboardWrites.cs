using System.Runtime.InteropServices;

namespace PinyinTranslate.Input;

/// <summary>
/// Sequence metadata only. A fully rendered clipboard can outlive its owner
/// window, so GetClipboardOwner==0 alone cannot exclude our own translation.
/// </summary>
public static class OwnClipboardWrites
{
    private static readonly object Gate = new();
    private static readonly Queue<(uint Sequence, long Time)> Recent = new();
    public static void Record(uint sequence)
    {
        if (sequence == 0) return;
        lock (Gate)
        {
            if (Recent.Count >= 64) Recent.Dequeue();
            Recent.Enqueue((sequence, Environment.TickCount64));
        }
    }
    public static bool Contains(uint sequence)
    {
        if (sequence == 0) return false;
        long now = Environment.TickCount64;
        lock (Gate)
        {
            while (Recent.TryPeek(out var item) && now - item.Time > 60000) Recent.Dequeue();
            return Recent.Any(item => item.Sequence == sequence);
        }
    }
    /// <summary>Call only immediately after an actual WPF/system clipboard write, never from statistics or simulated callbacks.</summary>
    public static void NoteNativeCopy()
    {
        try
        {
            nint owner = GetClipboardOwner();
            if (owner != 0)
            {
                _ = GetWindowThreadProcessId(owner, out uint process);
                if (process != Environment.ProcessId) return;
            }
            Record(GetClipboardSequenceNumber());
        }
        catch { /* Tracking must not turn a successful copy into a UI failure. */ }
    }
    [DllImport("user32.dll")] private static extern uint GetClipboardSequenceNumber();
    [DllImport("user32.dll")] private static extern nint GetClipboardOwner();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint process);
}
