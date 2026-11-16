using System.Buffers.Binary;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json;

namespace PinyinTranslate.Selection;

internal sealed record CopyClipboardWorkerRequest(long Window, int ProcessId, uint BeforeSequence,
    int ParentProcessId, long ParentStartTicks, int TimeoutMilliseconds);
internal sealed record CopyClipboardWorkerReply(CopySelectionStatus Status, uint Sequence, string? Text);

/// <summary>Private anonymous pipes only; copied text never appears in arguments or files.</summary>
internal static class CopyClipboardProtocol
{
    internal const int MaximumPayload = 65536;

    internal static async Task WriteAsync<T>(Stream stream, T value, CancellationToken cancellation)
    {
        byte[] data = JsonSerializer.SerializeToUtf8Bytes(value);
        if (data.Length > MaximumPayload) throw new InvalidDataException("Clipboard frame is too large.");
        byte[] header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, data.Length);
        await stream.WriteAsync(header, cancellation).ConfigureAwait(false);
        await stream.WriteAsync(data, cancellation).ConfigureAwait(false);
        await stream.FlushAsync(cancellation).ConfigureAwait(false);
    }

    internal static async Task<T> ReadAsync<T>(Stream stream, int maximumLength, CancellationToken cancellation)
    {
        byte[] header = new byte[4];
        await stream.ReadExactlyAsync(header, cancellation).ConfigureAwait(false);
        int length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length <= 0 || length > maximumLength) throw new InvalidDataException("Invalid clipboard frame size.");
        byte[] data = new byte[length];
        await stream.ReadExactlyAsync(data, cancellation).ConfigureAwait(false);
        return JsonSerializer.Deserialize<T>(data) ?? throw new InvalidDataException("Empty clipboard frame.");
    }
}

internal interface ICopyClipboardProcess : IDisposable
{
    Stream Input { get; }
    Stream Output { get; }
    int ExitCode { get; }
    Task WaitForExitAsync(CancellationToken cancellation);
    void Terminate();
}

/// <summary>Each copy owns its helper. Cancellation kills just that helper, releasing any clipboard lock.</summary>
internal sealed class CopyClipboardProcessCapture(Func<ICopyClipboardProcess>? start = null) : ICopyClipboardCapture
{
    public async Task<CopySelectionResult> ReadAsync(CopySelectionRequest request, TimeSpan timeout, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        using var child = (start ?? NativeProcess.Start)();
        // Redirected Windows process pipes may use synchronous handles. Killing
        // our helper also unblocks reads whose ReadAsync ignores cancellation.
        using var stopOnCancellation = cancellation.Register(child.Terminate);
        using var parent = Process.GetCurrentProcess();
        var message = new CopyClipboardWorkerRequest(request.Window.ToInt64(), request.ProcessId, request.BeforeSequence,
            Environment.ProcessId, parent.StartTime.ToUniversalTime().Ticks, checked((int)timeout.TotalMilliseconds));
        await CopyClipboardProtocol.WriteAsync(child.Input, message, cancellation).ConfigureAwait(false);
        var reply = await CopyClipboardProtocol.ReadAsync<CopyClipboardWorkerReply>(child.Output,
            CopyClipboardProtocol.MaximumPayload, cancellation).ConfigureAwait(false);
        await child.WaitForExitAsync(cancellation).ConfigureAwait(false);
        if (child.ExitCode != 0 || !Enum.IsDefined(reply.Status)) return new(CopySelectionStatus.Unavailable);
        if (reply.Status != CopySelectionStatus.Success) return new(reply.Status);
        if (reply.Sequence == 0 || reply.Sequence == request.BeforeSequence || string.IsNullOrWhiteSpace(reply.Text) ||
            reply.Text.Length > WindowsCopiedSelectionReader.MaximumTextLength) return new(CopySelectionStatus.NoNewText);
        return new(CopySelectionStatus.Success, new(request.Window, request.ProcessId, reply.Sequence, reply.Text));
    }

    internal static ProcessStartInfo CreateStartInfo(string processPath, string assemblyPath, string assemblyName)
    {
        if (assemblyName != "PinyinTranslate.App" || !Path.IsPathFullyQualified(processPath))
            throw new InvalidOperationException("The clipboard helper requires the application host.");
        var info = new ProcessStartInfo(processPath)
        {
            UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardInput = true, RedirectStandardOutput = true
        };
        if (Path.GetFileNameWithoutExtension(processPath).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
        {
            if (!Path.IsPathFullyQualified(assemblyPath)) throw new InvalidOperationException("Missing application assembly.");
            info.ArgumentList.Add(assemblyPath);
        }
        info.ArgumentList.Add(CopyClipboardWorker.Argument);
        return info;
    }

    private sealed class NativeProcess : ICopyClipboardProcess
    {
        private readonly Process process;
        private int stopped;
        private NativeProcess(Process process) => this.process = process;
        internal static ICopyClipboardProcess Start()
        {
            Assembly entry = Assembly.GetEntryAssembly() ?? throw new InvalidOperationException("Missing application entry.");
            var info = CreateStartInfo(Environment.ProcessPath ?? "", entry.Location, entry.GetName().Name ?? "");
            return new NativeProcess(Process.Start(info) ?? throw new InvalidOperationException("Clipboard helper did not start."));
        }
        public Stream Input => process.StandardInput.BaseStream;
        public Stream Output => process.StandardOutput.BaseStream;
        public int ExitCode => process.ExitCode;
        public Task WaitForExitAsync(CancellationToken cancellation) => process.WaitForExitAsync(cancellation);
        public void Terminate()
        {
            if (Interlocked.Exchange(ref stopped, 1) != 0) return;
            try
            {
                if (!process.HasExited) process.Kill();
            }
            catch (InvalidOperationException) { }
            catch (System.ComponentModel.Win32Exception) { }
        }
        public void Dispose()
        {
            Terminate();
            try { _ = process.WaitForExit(250); }
            catch (InvalidOperationException) { }
            catch (System.ComponentModel.Win32Exception) { }
            finally { process.Dispose(); }
        }
    }
}

/// <summary>Early application entry point: no WPF, updater, mutex, UIA, hooks, or translation services.</summary>
public static class CopyClipboardWorker
{
    public const string Argument = "--copy-clipboard-worker";

    public static int Run()
    {
        try { return RunAsync(Console.OpenStandardInput(), Console.OpenStandardOutput()).GetAwaiter().GetResult(); }
        catch { return 5; }
    }

    private static async Task<int> RunAsync(Stream input, Stream output)
    {
        using var startup = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var message = await CopyClipboardProtocol.ReadAsync<CopyClipboardWorkerRequest>(input, 1024, startup.Token).ConfigureAwait(false);
        if (message.Window == 0 || message.ProcessId <= 0 || message.ParentProcessId <= 0 ||
            message.ParentProcessId == Environment.ProcessId || message.ProcessId == message.ParentProcessId ||
            message.TimeoutMilliseconds is < 1 or > 10000) return 2;
        using var parent = Process.GetProcessById(message.ParentProcessId);
        if (parent.HasExited || parent.StartTime.ToUniversalTime().Ticks != message.ParentStartTicks) return 3;
        using var lifetime = new CancellationTokenSource(TimeSpan.FromMilliseconds(message.TimeoutMilliseconds + 500));
        using var reader = WindowsCopiedSelectionReader.CreateNativeWorker(message.ParentProcessId,
            TimeSpan.FromMilliseconds(message.TimeoutMilliseconds));
        try
        {
            var request = new CopySelectionRequest(checked((nint)message.Window), message.ProcessId, message.BeforeSequence);
            Task<CopySelectionResult> read = reader.ReadAfterCopyAsync(request, () => !lifetime.IsCancellationRequested, lifetime.Token);
            Task parentExit = parent.WaitForExitAsync(lifetime.Token);
            if (await Task.WhenAny(read, parentExit).ConfigureAwait(false) != read) return 3;
            var result = await read.ConfigureAwait(false);
            await CopyClipboardProtocol.WriteAsync(output, new CopyClipboardWorkerReply(result.Status,
                result.Selection?.Sequence ?? 0, result.Selection?.Text), lifetime.Token).ConfigureAwait(false);
            return 0;
        }
        finally { await lifetime.CancelAsync().ConfigureAwait(false); }
    }
}
