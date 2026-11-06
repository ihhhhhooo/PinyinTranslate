using System.Reflection;
using System.IO;
using System.Runtime.Loader;
using System.Text.Json;
using PinyinTranslate.App;
using PinyinTranslate.Input;
using PinyinTranslate.Screenshot.Tests;

// CLI-only differential probe. It never runs either application's constructor,
// entry point or Dispose, and never creates a UIA/clipboard/keyboard service.
internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        if (args.Length != 1 || !Path.IsPathFullyQualified(args[0]) || !File.Exists(args[0])) return 2;
        var legacyContext = new LegacyContext();
        try
        {
            Type oldRuntime = legacyContext.LoadFromAssemblyPath(args[0]).GetType("PinyinTranslate.App.ImeRuntimeController", true)!;
            Type newRuntime = typeof(DesktopServices).Assembly.GetType("PinyinTranslate.App.ImeRuntimeController", true)!;
            var oldResult = await ProbeAsync(oldRuntime, "shipped");
            var newResult = await ProbeAsync(newRuntime, "fixed");
            Console.WriteLine(JsonSerializer.Serialize(oldResult));
            Console.WriteLine(JsonSerializer.Serialize(newResult));
            return oldResult.TranslationReady && !oldResult.AcceptedTab && oldResult.WriterCalls == 0 &&
                newResult.TranslationReady && newResult.AcceptedTab && newResult.WriterCalls == 1 && newResult.Verified ? 0 : 1;
        }
        finally { legacyContext.Unload(); }
    }

    private sealed record Result(string Version, bool TranslationReady, bool AcceptedTab, int WriterCalls,
        int ClipboardPreparations, bool Verified, bool PreservedPrefixAndSuffix);

    private static async Task<Result> ProbeAsync(Type runtimeType, string version)
    {
        var observer = new FakeObserver();
        using var fixture = new HeadlessRuntimeFixture((_, _, _) => observer, runtimeType);
        var baseline = new EditableContextSnapshot
        {
            ForegroundWindow = 1, ForegroundProcessId = Environment.ProcessId, FocusProcessId = Environment.ProcessId,
            FocusRuntimeId = "synthetic-editor", AutomationId = "synthetic-editor", FrameworkId = "Synthetic", ClassName = "Edit",
            HasTextPattern = true, HasValuePattern = true, HasCompleteText = true, HasSingleSelection = true,
            Value = "PREFIX | SUFFIX", DocumentText = "PREFIX | SUFFIX", SelectionStartUtf16 = 7, SelectionEndUtf16 = 7,
            IsReadOnly = false, IsReadOnlyKnown = true, CanPreview = true, CanReplace = true,
            CommitMethod = EditableCommitMethod.CancelCompositionAndPaste
        };
        var composing = baseline with { Value = "PREFIX shuyu| SUFFIX", DocumentText = "PREFIX shuyu| SUFFIX",
            SelectionStartUtf16 = 12, SelectionEndUtf16 = 12 };
        fixture.TranslateAsync = (_, _) => Task.FromResult("belongs to");
        fixture.Enable(); fixture.SetContext(baseline);
        await fixture.InvokeAsync("BindAsync", baseline, 1L, CancellationToken.None);
        fixture.Invoke("OnKeyStroke", new KeyStroke(0x53, true, KeyModifiers.None, false, 1));
        // Reproduce the old startup/focus ordering after a real baseline was
        // captured: revoke the old request, then observe the same editor anew.
        object binding = fixture.Get("_binding")!;
        fixture.Invoke("OnFocusInvalidated", binding);
        fixture.SetContext(composing);
        int writes = 0, clipboardPreparations = 0;
        bool verified = false;
        EditableContextSnapshot current = composing;
        fixture.ReplaceAsync = (expected, cached, text, token, scope, _) =>
        {
            writes++;
            long clock = 0;
            var workflow = new VerifiedPasteWorkflow(new(0, 1, 50, 20), milliseconds => clock += milliseconds, () => clock);
            var backend = new FakeInsertion(() => current, next => { current = next; fixture.Context = next; }, () => clipboardPreparations++);
            var result = workflow.Execute(expected, cached, text, token, scope, () => current, () =>
            {
                current = baseline; fixture.Context = current;
                return new(true, true, true, "SyntheticCancel");
            }, backend);
            verified = result.Succeeded;
            return Task.FromResult(result);
        };
        var candidate = new CandidateObservation(1, 1, Environment.ProcessId, "synthetic-editor", "属于",
            Array.AsReadOnly(new[] { "属于" }), new(1, 1, 200, 40));
        await fixture.InvokeAsync("ObserveAsync", binding, candidate);
        await WaitAsync(() => fixture.Coordinator.State.Translation == "belongs to");
        bool translationReady = fixture.Coordinator.State.Translation == "belongs to";
        bool accepted = (bool)fixture.Invoke("TryAcceptTab", (nint)1)!;
        if (accepted) await WaitAsync(() => fixture.Coordinator.State.Observation is null);
        if (!fixture.Faults.IsEmpty) throw new AggregateException(fixture.Faults);
        return new(version, translationReady, accepted, writes, clipboardPreparations, verified,
            current.PlainText.StartsWith("PREFIX ", StringComparison.Ordinal) && current.PlainText.EndsWith("| SUFFIX", StringComparison.Ordinal));
    }

    private static async Task WaitAsync(Func<bool> condition)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(4));
        while (!condition()) await Task.Delay(5, deadline.Token);
    }

    private sealed class FakeInsertion(Func<EditableContextSnapshot> read, Action<EditableContextSnapshot> write, Action prepare) : ITextInsertionBackend
    {
        public TextInsertionResult Insert(string text, Func<bool> authorize, Func<bool> verify, CancellationToken token)
        {
            if (token.IsCancellationRequested || !authorize()) return new(false, false, false, "SyntheticDenied", ClipboardRestorationStatus.NotUsed);
            prepare();
            var before = read();
            int caret = before.SelectionStartUtf16;
            string value = before.PlainText[..caret] + text + before.PlainText[caret..];
            write(before with { Value = value, DocumentText = value, SelectionStartUtf16 = caret + text.Length, SelectionEndUtf16 = caret + text.Length });
            return new(true, true, verify(), "SyntheticPaste", ClipboardRestorationStatus.Restored);
        }
    }

    private sealed class FakeObserver : ICandidateObserver
    {
        public Task<bool> Ready => Task.FromResult(true);
        public bool IsRunning => true;
        public bool SetEnabled(bool enabled) => true;
        public bool IsCurrent(long version) => true;
        public void NotifyInputChanged() { }
        public Task<bool> ValidateAsync(CandidateObservation snapshot, CancellationToken token = default) => Task.FromResult(true);
        public event Action<long>? Invalidated { add { } remove { } }
        public event Action? FocusInvalidated { add { } remove { } }
        public event Action<CandidateObservation>? Observed { add { } remove { } }
        public event Action<long>? CandidatesCleared { add { } remove { } }
        public event Action<string>? Status { add { } remove { } }
        public void Dispose() { }
    }

    private sealed class LegacyContext() : AssemblyLoadContext("headless-shipped-runtime", true)
    {
        // Keep the compatibility DTOs and delegates in the default context so
        // both runtime versions use the same audited managed fake boundaries.
        protected override Assembly? Load(AssemblyName name) => Default.LoadFromAssemblyName(name);
    }
}
