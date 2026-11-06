using System.Reflection;
using System.Runtime.CompilerServices;
using PinyinTranslate.Input;

namespace PinyinTranslate.Core.Tests;

// Only the hook's managed state transition method is exercised. No constructor,
// message loop, native hook, foreground lookup, clipboard or input API is invoked.
[Trait("Category", "HeadlessOnly")]
public sealed class ImeTabInputProtocolTests
{
    [Fact]
    public void AcceptedPlainTabDoesNotReachTheOrdinaryInvalidationHandler()
    {
        var test = new HookFixture();
        Assert.True(test.Key(0x09, true));
        Assert.True(test.Key(0x09, true));
        Assert.True(test.Key(0x09, false));
        Assert.Equal(1, test.AcceptAttempts);
        Assert.Empty(test.Strokes);
        Assert.Equal(new[] { "try-accept" }, test.Events);
    }

    [Fact]
    public void RejectedTabIsDeliveredToTheOrdinaryHandlerAfterAcceptanceWasRejected()
    {
        var test = new HookFixture { Accept = false };
        Assert.False(test.Key(0x09, true));
        Assert.Equal(new[] { "try-accept", "ordinary-down" }, test.Events);
        Assert.Single(test.Strokes);
        Assert.Equal(0x09u, test.Strokes[0].VirtualKey);
        Assert.False(test.Strokes[0].IsRepeat);
        Assert.False(test.Key(0x09, true));
        Assert.True(test.Strokes[^1].IsRepeat);
        Assert.Equal(1, test.AcceptAttempts); // No late retry during the same press.
    }

    [Fact]
    public void ARejectedPressCannotBeAcceptedByAutoRepeatAfterReadinessChanges()
    {
        var test = new HookFixture { Accept = false };
        Assert.False(test.Key(0x09, true));
        test.Accept = true;
        Assert.False(test.Key(0x09, true));
        Assert.Equal(1, test.AcceptAttempts);
        Assert.False(test.Key(0x09, false));
        Assert.True(test.Key(0x09, true));
        Assert.Equal(2, test.AcceptAttempts);
    }

    [Fact]
    public void ModifierReleaseWhileTabIsHeldDoesNotConvertTheHeldPressIntoPlainTab()
    {
        var test = new HookFixture();
        Assert.False(test.Key(0x10, true, scan: 0x36));
        Assert.Equal(KeyModifiers.Shift, test.Strokes[^1].Modifiers);
        Assert.False(test.Key(0x09, true));
        Assert.Equal(0, test.AcceptAttempts);
        Assert.False(test.Key(0x10, false, scan: 0x36));
        Assert.Equal(KeyModifiers.None, test.Strokes[^1].Modifiers);
        Assert.False(test.Key(0x09, true));
        Assert.Equal(0, test.AcceptAttempts);
        Assert.False(test.Key(0x09, false));
        Assert.True(test.Key(0x09, true));
        Assert.Equal(1, test.AcceptAttempts);
    }

    [Fact]
    public void StartupModifierCacheRequiresTheMatchingSideRelease()
    {
        var test = new HookFixture();
        test.KeysDown[0xA2] = true;
        test.KeysDown[0xA3] = true;
        Assert.False(test.Key(0x11, false, scan: 0x1D, flags: 1));
        Assert.Equal(KeyModifiers.Control, test.Strokes[^1].Modifiers);
        Assert.False(test.Key(0x09, true));
        Assert.False(test.Key(0x09, false));
        Assert.Equal(0, test.AcceptAttempts);
        Assert.False(test.Key(0x11, false, scan: 0x1D));
        Assert.True(test.Key(0x09, true));
        Assert.Equal(1, test.AcceptAttempts);
    }

    [Fact]
    public void OwnMarkedControlVAndTabUpCannotInvalidateOrPoisonThePhysicalPress()
    {
        var test = new HookFixture();
        nuint marker = InputKeyboardHook.SyntheticInputMarker;
        Assert.False(test.Key(0x11, true, extra: marker));
        Assert.False(test.Key(0x56, true, extra: marker));
        Assert.False(test.Key(0x56, false, extra: marker));
        Assert.False(test.Key(0x11, false, extra: marker));
        Assert.Empty(test.Strokes);
        Assert.True(test.Key(0x09, true));
        Assert.False(test.Key(0x09, false, extra: marker));
        Assert.True(test.Key(0x09, true));
        Assert.True(test.Key(0x09, false));
        Assert.Equal(1, test.AcceptAttempts);
        Assert.Empty(test.Strokes);
    }

    [Fact]
    public void ForeignInjectedInputIsObservedAndItsModifierTransitionsRemainAccurate()
    {
        var test = new HookFixture();
        Assert.False(test.Key(0x11, true, scan: 0x1D, flags: 0x10));
        Assert.Equal(KeyModifiers.Control, test.Strokes[^1].Modifiers);
        Assert.False(test.Key(0x09, true, flags: 0x10));
        Assert.False(test.Key(0x09, false, flags: 0x10));
        Assert.Equal(0, test.AcceptAttempts);
        Assert.False(test.Key(0x11, false, scan: 0x1D, flags: 0x10));
        Assert.Equal(KeyModifiers.None, test.Strokes[^1].Modifiers);
        Assert.True(test.Key(0x09, true, flags: 0x10));
        Assert.Equal(1, test.AcceptAttempts);
    }

    [Fact]
    public void DisabledHookNeverAttemptsAcceptanceButStillReportsOrdinaryTab()
    {
        var test = new HookFixture();
        test.Hook.Enabled = false;
        Assert.False(test.Key(0x09, true));
        Assert.False(test.Key(0x09, false));
        Assert.Equal(0, test.AcceptAttempts);
        Assert.Equal(2, test.Strokes.Count);
    }

    [Fact]
    public void CallbackFailuresFailOpenWithoutLeakingExceptions()
    {
        var test = new HookFixture { ThrowOnAccept = true, ThrowOnOrdinary = true };
        Assert.False(test.Key(0x09, true));
        Assert.False(test.Key(0x09, false));
        Assert.Equal(1, test.AcceptAttempts);
        Assert.Equal(2, test.Strokes.Count);
    }

    private sealed class HookFixture
    {
        private static readonly Type DataType = typeof(InputKeyboardHook).GetNestedType("KeyboardData", BindingFlags.NonPublic)!;
        private static readonly MethodInfo Process = typeof(InputKeyboardHook).GetMethod("ProcessKeyEvent", BindingFlags.Instance | BindingFlags.NonPublic)!;
        public InputKeyboardHook Hook { get; } = (InputKeyboardHook)RuntimeHelpers.GetUninitializedObject(typeof(InputKeyboardHook));
        public bool[] KeysDown { get; } = new bool[256];
        public List<KeyStroke> Strokes { get; } = [];
        public List<string> Events { get; } = [];
        public bool Accept = true, ThrowOnAccept, ThrowOnOrdinary;
        public int AcceptAttempts;
        public HookFixture()
        {
            Set("_keysDown", KeysDown);
            Set("_tryAcceptTab", (Func<nint, bool>)(_ =>
            {
                AcceptAttempts++; Events.Add("try-accept");
                if (ThrowOnAccept) throw new InvalidOperationException();
                return Accept;
            }));
            Set("_onKeyStroke", (Action<KeyStroke>)(stroke =>
            {
                Strokes.Add(stroke); Events.Add(stroke.IsKeyDown ? "ordinary-down" : "ordinary-up");
                if (ThrowOnOrdinary) throw new InvalidOperationException();
            }));
            Hook.Enabled = true;
        }
        public bool Key(uint key, bool down, uint scan = 0, uint flags = 0, nuint extra = 0)
        {
            object data = Activator.CreateInstance(DataType)!;
            DataType.GetField("VirtualKey")!.SetValue(data, key);
            DataType.GetField("ScanCode")!.SetValue(data, scan);
            DataType.GetField("Flags")!.SetValue(data, flags);
            DataType.GetField("ExtraInfo")!.SetValue(data, extra);
            return (bool)Process.Invoke(Hook, [data, down, (nint)0x1234])!;
        }
        private void Set(string field, object value) => typeof(InputKeyboardHook).GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(Hook, value);
    }
}
