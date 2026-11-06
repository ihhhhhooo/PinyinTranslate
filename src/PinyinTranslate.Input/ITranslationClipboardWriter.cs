namespace PinyinTranslate.Input;

public sealed record TranslationClipboardCopyResult(bool Copied, bool ClipboardWriteAttempted, string Reason)
{
    public TranslationClipboardCopyResult(bool copied, string reason) : this(copied, copied, reason) { }
}

public interface ITranslationClipboardWriter : IDisposable
{
    /// <summary>stillCurrent may run on the clipboard worker; it must be fast, thread-safe and contain no COM/UIA work.</summary>
    Task<TranslationClipboardCopyResult> CopyAsync(string text, Func<bool> stillCurrent, CancellationToken token);
}

public sealed record TranslationClipboardWriterOptions(int MaximumOpenAttempts = 6, int RetryDelayMilliseconds = 20, int QueueCapacity = 8);

/// <summary>Injectable low-level clipboard operations. Tests use managed memory handles only.</summary>
public interface ITranslationClipboardCopyApi
{
    nint CreateOwnerWindow();
    bool DestroyOwnerWindow(nint owner);
    nint AllocateUnicodeData(ReadOnlyMemory<byte> nullTerminatedUtf16);
    void FreeData(nint memory);
    bool TryOpen(nint owner);
    bool Close();
    bool Empty();
    /// <summary>On success the system owns memory; destroying the owner must not free the published data.</summary>
    bool SetUnicodeText(nint memory);
    uint GetSequenceNumber() => 0;
}
