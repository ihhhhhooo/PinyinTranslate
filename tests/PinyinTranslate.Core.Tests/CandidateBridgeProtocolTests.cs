using System.Buffers.Binary;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using PinyinTranslate.Input;

namespace PinyinTranslate.Core.Tests;

public sealed class CandidateBridgeProtocolTests
{
    [Fact]
    public async Task FramingRoundTripsWindowAsInt64AndKeepsTheInputFence()
    {
        var expected = Observation(inputSequence: 11);
        await using var stream = new MemoryStream();
        await CandidateBridgeProtocol.WriteAsync(stream, expected, CancellationToken.None);
        stream.Position = 0;
        var actual = await CandidateBridgeProtocol.ReadAsync(stream, CancellationToken.None);
        Assert.Equal(expected.SessionId, actual.SessionId);
        Assert.Equal(expected.Epoch, actual.Epoch);
        Assert.Equal(expected.InputSequence, actual.InputSequence);
        Assert.Equal(expected.Observation!.ForegroundWindow, actual.Observation!.ForegroundWindow);
        Assert.Equal(expected.Observation.Candidate, actual.Observation.Candidate);
    }

    [Fact]
    public async Task FramingRejectsOversizedAndTruncatedMessages()
    {
        byte[] header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, CandidateBridgeProtocol.MaximumFrameBytes + 1);
        await using var oversized = new MemoryStream(header);
        await Assert.ThrowsAsync<InvalidDataException>(() => CandidateBridgeProtocol.ReadAsync(oversized, CancellationToken.None));
        BinaryPrimitives.WriteInt32LittleEndian(header, 10);
        await using var truncated = new MemoryStream(header.Concat(new byte[] { 1, 2 }).ToArray());
        await Assert.ThrowsAsync<EndOfStreamException>(() => CandidateBridgeProtocol.ReadAsync(truncated, CancellationToken.None));
    }

    [Fact]
    public async Task InputImmediatelyRevokesEvidenceAndOldPipeObservationCannotResurrectIt()
    {
        var fixture = new LocalClientFixture();
        Assert.True(fixture.Client.IsCurrent(100));
        fixture.Client.NotifyInputChanged();
        Assert.False(fixture.Client.IsCurrent(100));
        Assert.True(fixture.Commands.Reader.TryRead(out var barrier));
        Assert.Equal("input", barrier!.Kind);
        Assert.Equal(11, barrier.InputSequence);

        await fixture.ReceiveAsync(Observation(inputSequence: 10));
        Assert.False(fixture.Client.IsCurrent(101));
        await fixture.ReceiveAsync(Observation(inputSequence: 11));
        Assert.True(fixture.Client.IsCurrent(102));
        Assert.False(fixture.Client.IsCurrent(100));
    }

    [Fact]
    public async Task QueuedOldFocusAndStatusAreDiscardedAfterANewerInputBarrier()
    {
        var fixture = new LocalClientFixture();
        int focusEvents = 0, statusEvents = 0;
        fixture.Client.FocusInvalidated += () => focusEvents++;
        fixture.Client.Status += _ => statusEvents++;
        await fixture.ReceiveAsync(new() { Kind = "focus", SessionId = "test-session", Epoch = 5, InputSequence = 10 });
        await fixture.ReceiveAsync(new() { Kind = "status", SessionId = "test-session", Epoch = 5, InputSequence = 10, Status = "old" });
        fixture.Client.NotifyInputChanged();
        while (fixture.Notifications.Reader.TryRead(out var notification)) notification();
        Assert.Equal(0, focusEvents);
        Assert.Equal(0, statusEvents);
    }

    [Fact]
    public async Task AnotherSessionOrEpochCannotPublishEvidence()
    {
        var fixture = new LocalClientFixture();
        fixture.Client.NotifyInputChanged();
        await fixture.ReceiveAsync(Observation(11) with { SessionId = "other-session" });
        await fixture.ReceiveAsync(Observation(11) with { Epoch = 4 });
        Assert.False(fixture.Client.IsCurrent(101));
    }

    [Fact]
    public async Task RealFocusLossSurvivesAnOrdinaryInvalidationInTheSameInputSequence()
    {
        var fixture = new LocalClientFixture();
        int focusEvents = 0;
        fixture.Client.FocusInvalidated += () => focusEvents++;
        await fixture.ReceiveAsync(new() { Kind = "focus", SessionId = "test-session", Epoch = 5, InputSequence = 10 });
        await fixture.ReceiveAsync(new() { Kind = "invalidated", SessionId = "test-session", Epoch = 5, InputSequence = 10, SourceVersion = 10 });
        while (fixture.Notifications.Reader.TryRead(out var notification)) notification();
        Assert.Equal(1, focusEvents);
    }

    [Fact]
    public async Task LateInvalidationForAnAlreadyPublishedSourceCannotEraseThatStableSnapshot()
    {
        var fixture = new LocalClientFixture();
        await fixture.ReceiveAsync(Observation(10));
        Assert.True(fixture.Client.IsCurrent(101));
        await fixture.ReceiveAsync(new() { Kind = "invalidated", SessionId = "test-session", Epoch = 5, InputSequence = 10, SourceVersion = 9 });
        Assert.True(fixture.Client.IsCurrent(101));
    }

    [Fact]
    public void ResolverRejectsAnApphostWithoutItsManagedAssemblyAndRuntimeConfiguration()
    {
        string directory = Path.Combine(Path.GetTempPath(), "PinyinTranslate.BridgePackageTest." + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string executable = Path.Combine(directory, "PinyinTranslate.ImeBridge.exe");
        string assembly = Path.ChangeExtension(executable, ".dll");
        string configuration = Path.ChangeExtension(executable, ".runtimeconfig.json");
        bool IsComplete() => (bool)typeof(CandidateObserverClient).GetMethod("IsCompleteBridgePackage", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, [executable])!;
        try
        {
            File.WriteAllText(executable, "");
            Assert.False(IsComplete());
            File.WriteAllText(configuration, "{}");
            Assert.False(IsComplete());
            File.WriteAllText(assembly, "");
            Assert.True(IsComplete());
            File.Delete(configuration);
            Assert.False(IsComplete());
        }
        finally
        {
            File.Delete(executable); File.Delete(assembly); File.Delete(configuration);
            Directory.Delete(directory);
        }
    }

    private static CandidateBridgeMessage Observation(long inputSequence) => new()
    {
        Kind = "observed", SessionId = "test-session", Epoch = 5, InputSequence = inputSequence, SourceVersion = 9,
        Observation = new(9, 0x1234, 42, "runtime", "是", ["是", "时"], new(10, 20, 200, 100))
    };

    // No constructor is invoked: these tests never start a process, install UIA,
    // touch the desktop, or connect a pipe. Only decoded-frame/local-state logic runs.
    private sealed class LocalClientFixture
    {
        public CandidateObserverClient Client { get; } = (CandidateObserverClient)RuntimeHelpers.GetUninitializedObject(typeof(CandidateObserverClient));
        public Channel<CandidateBridgeMessage> Commands { get; } = Channel.CreateUnbounded<CandidateBridgeMessage>();
        public Channel<Action> Notifications { get; } = Channel.CreateUnbounded<Action>();
        public LocalClientFixture()
        {
            Set("_gate", new object()); Set("_sessionId", "test-session"); Set("_runtimeId", "runtime");
            Set("_commands", Commands); Set("_notifications", Notifications); Set("_targetPid", 42);
            Set("_validations", Activator.CreateInstance(Field("_validations").FieldType)!);
            Set("_epoch", 5L); Set("_inputSequence", 10L); Set("_version", 100L);
            Set("_sourceFloor", long.MinValue); Set("_lastPublishedSource", long.MinValue);
            Set("_enabled", true); Set("_running", true); Set("_hasEvidence", true);
        }
        public async Task ReceiveAsync(CandidateBridgeMessage message)
        {
            await using var stream = new MemoryStream();
            await CandidateBridgeProtocol.WriteAsync(stream, message, CancellationToken.None);
            stream.Position = 0;
            var read = (Task)typeof(CandidateObserverClient).GetMethod("ReadEventsAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(Client, [stream, CancellationToken.None])!;
            await Assert.ThrowsAsync<EndOfStreamException>(() => read);
        }
        private static FieldInfo Field(string name) => typeof(CandidateObserverClient).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!;
        private void Set(string field, object value) => Field(field).SetValue(Client, value);
    }
}
