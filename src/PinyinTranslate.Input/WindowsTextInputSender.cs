using System.Runtime.InteropServices;

namespace PinyinTranslate.Input;

/// <summary>Lazy Win32 sender; construction does not inspect or send system input.</summary>
public sealed class WindowsTextInputSender : ITextInputSender
{
    public TextInputDispatchResult SendPaste(Func<bool> stillAuthorized, CancellationToken cancellationToken) =>
        Dispatch(CreatePastePlan(), stillAuthorized, cancellationToken, cleanUpPaste: true);

    public TextInputDispatchResult SendUnicode(string text, Func<bool> stillAuthorized, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(text) || text.Length > GenericTextInsertionBackend.MaximumTextLength)
            return new(false, 0, 0, false, "InvalidUnicodeText");
        for (int index = 0; index < text.Length; index++)
        {
            if (char.IsControl(text[index])) return new(false, 0, 0, false, "UnicodeControlCharacterRejected");
            if (char.IsHighSurrogate(text[index]))
            {
                if (++index >= text.Length || !char.IsLowSurrogate(text[index])) return new(false, 0, 0, false, "InvalidUnicodeSurrogate");
            }
            else if (char.IsLowSurrogate(text[index])) return new(false, 0, 0, false, "InvalidUnicodeSurrogate");
        }
        var inputs = new NativeInput[text.Length * 2];
        for (int index = 0; index < text.Length; index++)
        {
            inputs[index * 2] = Key(0, text[index], 4);
            inputs[index * 2 + 1] = Key(0, text[index], 4 | 2);
        }
        return Dispatch(inputs, stillAuthorized, cancellationToken);
    }

    private static TextInputDispatchResult Dispatch(NativeInput[] inputs, Func<bool> authorize, CancellationToken cancellation, bool cleanUpPaste = false)
    {
        bool attempted = false;
        try
        {
            ArgumentNullException.ThrowIfNull(authorize);
            if (cancellation.IsCancellationRequested || !authorize()) return new(false, (uint)inputs.Length, 0, false, "NotAuthorized");
            if (HasModifier()) return new(false, (uint)inputs.Length, 0, false, "ModifierHeld");
            if (cancellation.IsCancellationRequested) return new(false, (uint)inputs.Length, 0, false, "CancelledAtDispatch");
            attempted = true;
            uint sent = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<NativeInput>());
            int error = Marshal.GetLastWin32Error();
            string reason = sent == inputs.Length ? "Sent" : $"PartialOrRejected:{sent}/{inputs.Length}:Win32={error}";
            if (cleanUpPaste && sent < inputs.Length)
            {
                var cleanup = CreatePasteCleanupPlan(sent);
                if (cleanup.Length > 0)
                {
                    // Only release keys whose down was in the successful prefix.
                    // This is one bounded key-up batch, never another paste/text attempt.
                    // It remains necessary when ordinary authorization is revoked.
                    try
                    {
                        uint released = SendInput((uint)cleanup.Length, cleanup, Marshal.SizeOf<NativeInput>());
                        reason += $":KeyUpCleanup={released}/{cleanup.Length}";
                    }
                    catch (Exception cleanupError) { reason += ":KeyUpCleanupUncertain:" + cleanupError.GetType().Name; }
                }
            }
            return new(true, (uint)inputs.Length, sent, false, reason);
        }
        catch (Exception error) { return new(attempted, (uint)inputs.Length, 0, attempted, "DispatchException:" + error.GetType().Name); }
    }

    private static NativeInput[] CreatePastePlan() =>
        [Key(0x11, 0, 0), Key(0x56, 0, 0), Key(0x56, 0, 2), Key(0x11, 0, 2)];

    private static NativeInput[] CreatePasteCleanupPlan(uint sent) => sent switch
    {
        1 or 3 => [Key(0x11, 0, 2)],
        2 => [Key(0x56, 0, 2), Key(0x11, 0, 2)],
        _ => []
    };

    private static bool HasModifier() =>
        (GetAsyncKeyState(0x10) & 0x8000) != 0 || (GetAsyncKeyState(0x11) & 0x8000) != 0 ||
        (GetAsyncKeyState(0x12) & 0x8000) != 0 || (GetAsyncKeyState(0x5B) & 0x8000) != 0 || (GetAsyncKeyState(0x5C) & 0x8000) != 0;

    private static NativeInput Key(ushort key, ushort scan, uint flags) => new()
    {
        Type = 1,
        Data = new NativeUnion { Keyboard = new KeyboardInput { VirtualKey = key, ScanCode = scan, Flags = flags, ExtraInfo = InputKeyboardHook.SyntheticInputMarker } }
    };
    [StructLayout(LayoutKind.Sequential)] private struct NativeInput { public uint Type; public NativeUnion Data; }
    [StructLayout(LayoutKind.Explicit)] private struct NativeUnion
    {
        [FieldOffset(0)] public KeyboardInput Keyboard;
        [FieldOffset(0)] public MouseInput Mouse;
    }
    [StructLayout(LayoutKind.Sequential)] private struct KeyboardInput
    {
        public ushort VirtualKey, ScanCode;
        public uint Flags, Time;
        public nuint ExtraInfo;
    }
    [StructLayout(LayoutKind.Sequential)] private struct MouseInput
    {
        public int X, Y;
        public uint MouseData, Flags, Time;
        public nuint ExtraInfo;
    }
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint count, [In] NativeInput[] inputs, int size);
}
