using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace PinyinTranslate.Selection;

public sealed record NativeSelectionReadResult(nint WindowHandle, int ProcessId, string Text, int Start, int End,
    string DocumentFingerprint);

public sealed record NativeSelectionWindowInfo(nint WindowHandle, int ProcessId, nint RootWindow,
    nint ForegroundWindow, nint ForegroundRootWindow, int ForegroundProcessId, string ClassName, uint Style, bool IsUnicode);

public sealed record NativeSelectionReaderOptions(int MaximumDocumentLength = 65535, int MaximumSelectionLength = 4000,
    int MessageTimeoutMilliseconds = 50, int MaximumReadMilliseconds = 600);

/// <summary>Only read-only, system-marshalled messages; no remote memory or input operations.</summary>
public interface INativeSelectionReadApi
{
    bool TryGetWindowInfo(nint window, out NativeSelectionWindowInfo info);
    bool TryGetTextLength(nint window, int timeoutMilliseconds, out int length);
    bool TryGetText(nint window, int capacity, int timeoutMilliseconds, out string text, out int copiedCharacters);
    bool TryGetPackedSelection(nint window, int timeoutMilliseconds, out uint packedRange);
}

/// <summary>
/// Synchronous fallback for real Unicode Edit/RichEdit controls. Construction is
/// inert. The caller retains UIA/foreground authorization and all callbacks stay
/// on its reading thread. Unsupported providers fail closed, without Ctrl+C.
/// </summary>
public sealed class NativeSelectionReader
{
    private const uint EsPassword = 0x20;
    private static readonly HashSet<string> Classes = new(StringComparer.OrdinalIgnoreCase)
        { "Edit", "RichEdit20W", "RICHEDIT50W", "RichEditD2DPT" };
    private readonly INativeSelectionReadApi _api;
    private readonly NativeSelectionReaderOptions _options;
    private readonly Func<int> _ownProcessId;
    private readonly Func<long> _clock;
    private sealed record Capture(string Document, int Start, int End, int LengthUpperBound);

    public NativeSelectionReader(INativeSelectionReadApi? api = null, NativeSelectionReaderOptions? options = null,
        Func<int>? ownProcessId = null, Func<long>? clockMilliseconds = null)
    {
        _api = api ?? new Win32ReadApi();
        _options = options ?? new();
        if (_options.MaximumDocumentLength is < 1 or > 65535 ||
            _options.MaximumSelectionLength < 1 || _options.MaximumSelectionLength > _options.MaximumDocumentLength ||
            _options.MessageTimeoutMilliseconds is < 1 or > 1000 || _options.MaximumReadMilliseconds is < 1 or > 5000)
            throw new ArgumentOutOfRangeException(nameof(options));
        _ownProcessId = ownProcessId ?? (() => Environment.ProcessId);
        _clock = clockMilliseconds ?? (() => Environment.TickCount64);
    }

    public NativeSelectionReadResult? Read(nint window, Func<bool> stillAllowed, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(stillAllowed);
        if (window == 0 || window == new nint(0xffff) || token.IsCancellationRequested) return null;
        try
        {
            int ownProcess = _ownProcessId();
            if (ownProcess <= 0) return null;
            long started = _clock();
            NativeSelectionWindowInfo? identity = null;

            int Remaining()
            {
                long elapsed = _clock() - started;
                return elapsed < 0 || elapsed >= _options.MaximumReadMilliseconds ? 0 :
                    (int)(_options.MaximumReadMilliseconds - elapsed);
            }
            bool Guard()
            {
                if (token.IsCancellationRequested || Remaining() == 0 || !stillAllowed()) return false;
                if (!_api.TryGetWindowInfo(window, out var info) || info.WindowHandle != window || info.ProcessId <= 0 ||
                    info.ProcessId == ownProcess || info.ProcessId != info.ForegroundProcessId || info.RootWindow == 0 ||
                    info.ForegroundWindow == 0 || info.RootWindow != info.ForegroundRootWindow || !info.IsUnicode ||
                    (info.Style & EsPassword) != 0 || !Classes.Contains(info.ClassName)) return false;
                if (identity is not null && identity != info) return false;
                identity ??= info;
                return !token.IsCancellationRequested && Remaining() > 0 && stillAllowed();
            }
            int Timeout() => Guard() ? Math.Min(_options.MessageTimeoutMilliseconds, Remaining()) : 0;
            bool Selection(out uint packed)
            {
                packed = 0;
                int timeout = Timeout();
                return timeout > 0 && _api.TryGetPackedSelection(window, timeout, out packed) && Guard();
            }
            bool Length(out int length)
            {
                length = 0;
                int timeout = Timeout();
                return timeout > 0 && _api.TryGetTextLength(window, timeout, out length) && Guard() &&
                    length >= 0 && length <= _options.MaximumDocumentLength;
            }
            Capture? CaptureOnce()
            {
                if (!Selection(out uint first) || !Decode(first, out int start, out int end) ||
                    end - start > _options.MaximumSelectionLength || !Length(out int upper) || end > upper) return null;
                int timeout = Timeout();
                if (timeout == 0 || !_api.TryGetText(window, upper + 1, timeout, out string text, out int copied) || !Guard() ||
                    text is null || copied < 0 || copied != text.Length || copied > upper || text.Length > _options.MaximumDocumentLength ||
                    text.IndexOf('\0') >= 0 || end > text.Length || !Length(out int afterLength) || upper != afterLength ||
                    !Selection(out uint last) || first != last) return null;
                // WM_GETTEXTLENGTH is allowed to overestimate for ANSI/Unicode
                // interoperability; WM_GETTEXT's copied count is authoritative.
                return new(text, start, end, upper);
            }

            if (!Guard()) return null;
            var before = CaptureOnce();
            var after = before is null ? null : CaptureOnce();
            if (before is null || after is null || before != after || !Guard()) return null;
            string document = before.Document;
            if (!IsBoundary(document, before.Start) || !IsBoundary(document, before.End)) return null;
            // Do not guess index mappings for RichEdit paragraph expansion or
            // Edit's optional CR/CR/LF soft-wrap serialization.
            string ambiguousBreak = identity!.ClassName.Equals("Edit", StringComparison.OrdinalIgnoreCase) ? "\r\r\n" : "\r\n";
            int breakAt = document.IndexOf(ambiguousBreak, StringComparison.Ordinal);
            if (breakAt >= 0 && breakAt < before.End) return null;
            string selection = document[before.Start..before.End];
            if (string.IsNullOrWhiteSpace(selection) || !ValidUtf16(selection)) return null;
            string fingerprint = Convert.ToHexString(SHA256.HashData(MemoryMarshal.AsBytes(document.AsSpan())));
            return Guard() ? new(window, identity.ProcessId, selection, before.Start, before.End, fingerprint) : null;
        }
        catch (Exception) { return null; }
    }

    private static bool Decode(uint packed, out int start, out int end)
    {
        start = (int)(packed & 0xffff); end = (int)(packed >> 16);
        // -1 means an index exceeds the packed 16-bit limit. Never reinterpret
        // it as a real 65535..65535 range or try WM_USER pointer messages.
        return packed != uint.MaxValue && start < end;
    }
    private static bool IsBoundary(string text, int offset) => offset == 0 || offset == text.Length ||
        !(char.IsHighSurrogate(text[offset - 1]) && char.IsLowSurrogate(text[offset]));
    private static bool ValidUtf16(string text)
    {
        for (int index = 0; index < text.Length; index++)
        {
            if (char.IsHighSurrogate(text[index])) { if (++index >= text.Length || !char.IsLowSurrogate(text[index])) return false; }
            else if (char.IsLowSurrogate(text[index])) return false;
        }
        return true;
    }

    private sealed class Win32ReadApi : INativeSelectionReadApi
    {
        private const uint Flags = 0x1 | 0x2 | 0x20; // BLOCK | ABORTIFHUNG | ERRORONEXIT; never NOTIMEOUTIFNOTHUNG.
        public bool TryGetWindowInfo(nint window, out NativeSelectionWindowInfo info)
        {
            info = null!;
            var name = new StringBuilder(256);
            if (GetClassName(window, name, name.Capacity) == 0 || GetWindowThreadProcessId(window, out uint process) == 0) return false;
            nint foreground = GetForegroundWindow();
            if (foreground == 0 || GetWindowThreadProcessId(foreground, out uint foregroundProcess) == 0) return false;
            SetLastErrorNative(0);
            nint style = IntPtr.Size == 8 ? GetWindowLongPtr(window, -16) : GetWindowLong(window, -16);
            if (style == 0 && Marshal.GetLastWin32Error() != 0) return false;
            info = new(window, checked((int)process), GetAncestor(window, 2), foreground, GetAncestor(foreground, 2),
                checked((int)foregroundProcess), name.ToString(), unchecked((uint)(long)style), IsWindowUnicode(window));
            return true;
        }
        public bool TryGetTextLength(nint window, int timeoutMilliseconds, out int length)
        {
            length = 0;
            if (SendMessageTimeout(window, 0x000e, 0, 0, Flags, (uint)timeoutMilliseconds, out nuint result) == 0 || result > int.MaxValue) return false;
            length = (int)result;
            return true;
        }
        public bool TryGetText(nint window, int capacity, int timeoutMilliseconds, out string text, out int copiedCharacters)
        {
            text = ""; copiedCharacters = 0;
            var buffer = new StringBuilder(capacity);
            if (SendMessageTimeoutText(window, 0x000d, (nuint)capacity, buffer, Flags, (uint)timeoutMilliseconds, out nuint result) == 0 || result > int.MaxValue) return false;
            text = buffer.ToString(); copiedCharacters = (int)result;
            return true;
        }
        public bool TryGetPackedSelection(nint window, int timeoutMilliseconds, out uint packedRange)
        {
            bool success = SendMessageTimeout(window, 0x00b0, 0, 0, Flags, (uint)timeoutMilliseconds, out nuint result) != 0;
            packedRange = unchecked((uint)result);
            return success;
        }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(nint window, StringBuilder name, int capacity);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint processId);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern nint GetAncestor(nint window, uint flags);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsWindowUnicode(nint window);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)] private static extern nint GetWindowLongPtr(nint window, int index);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongW", SetLastError = true)] private static extern int GetWindowLong(nint window, int index);
    [DllImport("kernel32.dll", EntryPoint = "SetLastError")] private static extern void SetLastErrorNative(uint error);
    [DllImport("user32.dll", EntryPoint = "SendMessageTimeoutW", SetLastError = true)] private static extern nint SendMessageTimeout(nint window, uint message, nuint wParam, nint lParam, uint flags, uint timeout, out nuint result);
    [DllImport("user32.dll", EntryPoint = "SendMessageTimeoutW", CharSet = CharSet.Unicode, SetLastError = true)] private static extern nint SendMessageTimeoutText(nint window, uint message, nuint wParam, [Out] StringBuilder lParam, uint flags, uint timeout, out nuint result);
}
