using System.Buffers.Binary;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

namespace PinyinTranslate.Input;

/// <summary>Local IPC DTOs. HWND is explicitly Int64; IntPtr is not JSON serializable.</summary>
public sealed record CandidateBridgeObservation(long SourceVersion, long ForegroundWindow, int FocusProcessId,
    string FocusRuntimeId, string Candidate, string[] VisiblePage, CandidateBounds Bounds)
{
    public static CandidateBridgeObservation From(CandidateObservation value) => new(value.Version,
        value.ForegroundWindow.ToInt64(), value.FocusProcessId, value.FocusRuntimeId, value.Candidate,
        value.VisiblePage.ToArray(), value.Bounds);
    public CandidateObservation ToObservation(long version) => new(version, checked((nint)ForegroundWindow),
        FocusProcessId, FocusRuntimeId, Candidate, Array.AsReadOnly(VisiblePage.ToArray()), Bounds);
}

public sealed record CandidateBridgeMessage
{
    public int ProtocolVersion { get; init; } = CandidateBridgeProtocol.CurrentVersion;
    public string Kind { get; init; } = "";
    public string SessionId { get; init; } = "";
    public long Epoch { get; init; }
    public long InputSequence { get; init; }
    public long SourceVersion { get; init; }
    public long RequestId { get; init; }
    public int ParentProcessId { get; init; }
    public long ParentStartTicks { get; init; }
    public int TargetProcessId { get; init; }
    public long TargetStartTicks { get; init; }
    public string AutomationId { get; init; } = "";
    public string FocusRuntimeId { get; init; } = "";
    public bool Enabled { get; init; }
    public bool Success { get; init; }
    public int WorkerHealthVersion { get; init; }
    public string? Status { get; init; }
    public CandidateObserverFailure? Failure { get; init; }
    public CandidateBridgeObservation? Observation { get; init; }
}

public static class CandidateBridgeProtocol
{
    public const int CurrentVersion = 2;
    public const int MaximumFrameBytes = 65536;

    public static async Task WriteAsync(Stream stream, CandidateBridgeMessage message, CancellationToken cancellation)
    {
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(message);
        if (payload.Length > MaximumFrameBytes) throw new InvalidDataException("Bridge frame too large.");
        byte[] header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, payload.Length);
        await stream.WriteAsync(header, cancellation).ConfigureAwait(false);
        await stream.WriteAsync(payload, cancellation).ConfigureAwait(false);
        await stream.FlushAsync(cancellation).ConfigureAwait(false);
    }

    public static async Task<CandidateBridgeMessage> ReadAsync(Stream stream, CancellationToken cancellation)
    {
        byte[] header = new byte[4];
        await stream.ReadExactlyAsync(header, cancellation).ConfigureAwait(false);
        int length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length is <= 0 or > MaximumFrameBytes) throw new InvalidDataException("Invalid bridge frame length.");
        byte[] payload = new byte[length];
        await stream.ReadExactlyAsync(payload, cancellation).ConfigureAwait(false);
        var message = JsonSerializer.Deserialize<CandidateBridgeMessage>(payload) ?? throw new InvalidDataException("Missing bridge message.");
        if (message.ProtocolVersion != CurrentVersion) throw new InvalidDataException("Unsupported bridge protocol.");
        return message;
    }

    public static bool IsPipeClient(PipeStream pipe, int expectedPid) =>
        GetNamedPipeClientProcessId(pipe.SafePipeHandle, out uint pid) && pid == expectedPid;
    public static bool IsPipeServer(PipeStream pipe, int expectedPid) =>
        GetNamedPipeServerProcessId(pipe.SafePipeHandle, out uint pid) && pid == expectedPid;

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint processId);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint processId);
}
