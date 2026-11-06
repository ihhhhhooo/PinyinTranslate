namespace PinyinTranslate.Input;

public enum ClipboardRestorationStatus
{
    NotUsed,
    Restored,
    SkippedBecauseClipboardChanged,
    RetainedForUnconfirmedPaste,
    Failed
}

public sealed record TextInsertionResult(bool InputAttempted, bool AnyInputSent, bool Verified, string Reason,
    ClipboardRestorationStatus ClipboardRestoration);

public interface ITextInsertionBackend
{
    TextInsertionResult Insert(string text, Func<bool> stillAuthorized, Func<bool> verifyInserted, CancellationToken cancellationToken);
}

public sealed record ClipboardRestoreResult(ClipboardRestorationStatus Status, string Reason);

public interface IClipboardSession : IDisposable
{
    /// <summary>Read-only check of this prepared clipboard's nonzero sequence and owner.</summary>
    bool IsCurrent();
    /// <summary>Restore only if both the clipboard owner and sequence still match this session.</summary>
    ClipboardRestoreResult TryRestore();
}

public sealed record ClipboardPreparationResult(bool Prepared, IClipboardSession? Session, string Reason,
    ClipboardRestorationStatus Restoration = ClipboardRestorationStatus.NotUsed);

public interface IClipboardTransport
{
    /// <summary>Must never send keyboard input; unsupported formats must fail before clearing the clipboard.</summary>
    ClipboardPreparationResult TryPrepare(string text, Func<bool> stillAuthorized, CancellationToken cancellationToken);
}

public sealed record TextInputDispatchResult(bool InputAttempted, uint RequestedEvents, uint SentEvents,
    bool DeliveryUncertain, string Reason)
{
    public bool AnyInputSent => SentEvents > 0 || DeliveryUncertain;
    public bool Complete => InputAttempted && !DeliveryUncertain && RequestedEvents > 0 && SentEvents == RequestedEvents;
}

public interface ITextInputSender
{
    /// <summary>Recheck authorization and modifiers immediately before the single native batch.</summary>
    TextInputDispatchResult SendPaste(Func<bool> stillAuthorized, CancellationToken cancellationToken);
    TextInputDispatchResult SendUnicode(string text, Func<bool> stillAuthorized, CancellationToken cancellationToken);
}

/// <summary>
/// Synchronous insertion coordinator. Constructors have no OS side effects.
/// Verification runs on the caller's thread; injected test transports never need Windows.
/// Unicode fallback is confined to clipboard preparation, before any keyboard attempt.
/// </summary>
public sealed class GenericTextInsertionBackend : ITextInsertionBackend
{
    public event Action<ImeWriteProgress>? WriteProgress;
    public const int MaximumTextLength = 8192;
    private readonly IClipboardTransport _clipboard;
    private readonly ITextInputSender _keyboard;

    public GenericTextInsertionBackend() : this(new WindowsClipboardTransport(), new WindowsTextInputSender()) { }
    public GenericTextInsertionBackend(IClipboardTransport clipboard, ITextInputSender keyboard)
    {
        ArgumentNullException.ThrowIfNull(clipboard);
        ArgumentNullException.ThrowIfNull(keyboard);
        _clipboard = clipboard;
        _keyboard = keyboard;
    }

    public TextInsertionResult Insert(string text, Func<bool> stillAuthorized, Func<bool> verifyInserted, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stillAuthorized);
        ArgumentNullException.ThrowIfNull(verifyInserted);
        if (!IsValidText(text)) return new(false, false, false, "InvalidText", ClipboardRestorationStatus.NotUsed);
        if (!Authorized(stillAuthorized, cancellationToken)) return new(false, false, false, "NotAuthorized", ClipboardRestorationStatus.NotUsed);
        ClipboardPreparationResult preparation;
        Progress("clipboard", "Preparing");
        try { preparation = _clipboard.TryPrepare(text, stillAuthorized, cancellationToken); }
        catch (Exception error)
        {
            // The clipboard transport contract forbids keyboard input entirely.
            preparation = new(false, null, "ClipboardPreparationException:" + error.GetType().Name);
        }

        if (!preparation.Prepared || preparation.Session is null)
        {
            Progress("clipboard", "PreparationUnavailable");
            try { preparation.Session?.Dispose(); } catch (Exception) { }
            return UnicodeFallback(text, stillAuthorized, verifyInserted, cancellationToken, preparation);
        }

        var session = preparation.Session;
        Progress("clipboard", "Prepared");
        try
        {
            if (!Authorized(stillAuthorized, cancellationToken))
                return new(false, false, false, "NotAuthorizedBeforePaste", Restore(session).Status);
            if (!SessionCurrent(session))
                return new(false, false, false, "ClipboardChangedBeforePaste", Restore(session).Status);
            TextInputDispatchResult dispatch;
            // The sender repeats this combined check at its actual native boundary.
            // The target-text authorization remains on Insert's caller thread.
            bool PasteAuthorized() => Authorized(stillAuthorized, cancellationToken) && SessionCurrent(session);
            try { Progress("insertion", "PasteDispatch"); dispatch = _keyboard.SendPaste(PasteAuthorized, cancellationToken); }
            catch (Exception error)
            {
                return new(true, true, false, "PasteDispatchUncertain:" + error.GetType().Name, ClipboardRestorationStatus.RetainedForUnconfirmedPaste);
            }
            if (!dispatch.Complete)
            {
                var restoration = dispatch.AnyInputSent ? ClipboardRestorationStatus.RetainedForUnconfirmedPaste : Restore(session).Status;
                return new(dispatch.InputAttempted || dispatch.AnyInputSent, dispatch.AnyInputSent, false, "PasteNotCompleted:" + dispatch.Reason, restoration);
            }

            // Even after cancellation, verification may establish whether the
            // already-sent paste has completed. No additional input is sent here.
            Progress("verification", "CheckingPaste");
            if (!Verify(verifyInserted))
                return new(true, true, false, "PasteUnverified", ClipboardRestorationStatus.RetainedForUnconfirmedPaste);
            Progress("clipboard-restore", "Restoring");
            var restored = Restore(session);
            Progress("clipboard-restore", restored.Status.ToString());
            return new(true, true, true, "PasteVerified:" + restored.Reason, restored.Status);
        }
        finally
        {
            try { session.Dispose(); } catch (Exception) { }
        }
    }

    private TextInsertionResult UnicodeFallback(string text, Func<bool> stillAuthorized, Func<bool> verifyInserted,
        CancellationToken cancellationToken, ClipboardPreparationResult preparation)
    {
        if (!Authorized(stillAuthorized, cancellationToken))
            return new(false, false, false, "NotAuthorizedBeforeUnicode", preparation.Restoration);
        TextInputDispatchResult dispatch;
        Progress("insertion", "UnicodeFallbackDispatch");
        try { dispatch = _keyboard.SendUnicode(text, stillAuthorized, cancellationToken); }
        catch (Exception error) { return new(true, true, false, "UnicodeDispatchUncertain:" + error.GetType().Name, preparation.Restoration); }
        bool verified = dispatch.Complete && Verify(verifyInserted);
        return new(dispatch.InputAttempted || dispatch.AnyInputSent, dispatch.AnyInputSent, verified,
            (verified ? "UnicodeFallbackVerified:" : "UnicodeFallbackUnverified:") + preparation.Reason + ":" + dispatch.Reason,
            preparation.Restoration);
    }

    private static ClipboardRestoreResult Restore(IClipboardSession session)
    {
        try { return session.TryRestore(); }
        catch (Exception error) { return new(ClipboardRestorationStatus.Failed, "RestoreException:" + error.GetType().Name); }
    }

    private void Progress(string stage, string code)
    {
        try { WriteProgress?.Invoke(new(stage, code)); } catch (Exception) { }
    }

    private static bool SessionCurrent(IClipboardSession session)
    {
        try { return session.IsCurrent(); } catch (Exception) { return false; }
    }

    private static bool Authorized(Func<bool> authorize, CancellationToken cancellation)
    {
        try { return !cancellation.IsCancellationRequested && authorize() && !cancellation.IsCancellationRequested; }
        catch (Exception) { return false; }
    }

    private static bool Verify(Func<bool> verify)
    {
        try { return verify(); } catch (Exception) { return false; }
    }

    private static bool IsValidText(string? text)
    {
        if (string.IsNullOrEmpty(text) || text.Length > MaximumTextLength || text.IndexOf('\0') >= 0) return false;
        for (int index = 0; index < text.Length; index++)
        {
            if (char.IsHighSurrogate(text[index]))
            {
                if (++index >= text.Length || !char.IsLowSurrogate(text[index])) return false;
            }
            else if (char.IsLowSurrogate(text[index])) return false;
        }
        return true;
    }
}
