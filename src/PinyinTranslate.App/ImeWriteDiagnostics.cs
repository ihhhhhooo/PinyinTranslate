using System.IO;
using System.Text.Json;
using System.Threading.Channels;
using PinyinTranslate.Input;

namespace PinyinTranslate.App;

/// <summary>Bounded metadata-only diagnostics. Never reads the desktop or records text.</summary>
internal sealed class ImeWriteDiagnostics : IDisposable
{
    private readonly Channel<string> _lines = Channel.CreateBounded<string>(new BoundedChannelOptions(256)
        { SingleReader = true, FullMode = BoundedChannelFullMode.DropOldest, AllowSynchronousContinuations = false });
    private int _disposed;

    public ImeWriteDiagnostics() => _ = Task.Run(WriteAsync);

    public void Record(string stage, string code, EditableContextSnapshot? context = null, bool hasAnchor = false)
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        _lines.Writer.TryWrite(FormatLine(stage, code, context, hasAnchor, DateTimeOffset.UtcNow));
    }

    internal static string FormatLine(string stage, string code, EditableContextSnapshot? context, bool hasAnchor, DateTimeOffset timestamp) =>
        JsonSerializer.Serialize(new
        {
            timestamp,
            stage = Token(stage), code = Token(code), hasAnchor,
            context = context is null ? null : new
            {
                context.CanPreview, context.CanReplace, context.HasTextPattern, context.HasValuePattern,
                context.HasCompleteText, context.HasConsistentText, context.HasSingleSelection,
                context.IsReadOnly, context.IsReadOnlyKnown, context.IsPassword,
                composition = context.CompositionState.ToString(), context.HasActiveCompositionRange, context.HasActiveCaret,
                commitMethod = context.CommitMethod.ToString(), capability = Token(context.CapabilityReason),
                textLength = context.PlainText.Length, context.SelectionStartUtf16, context.SelectionEndUtf16
            }
        });

    private static string Token(string value) => new((value ?? "").Take(160)
        .Select(character => char.IsAsciiLetterOrDigit(character) || character is '.' or ':' or '_' or '-' ? character : '_').ToArray());

    private async Task WriteAsync()
    {
        string directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PinyinTranslate", "logs");
        string path = Path.Combine(directory, "ime-input.jsonl");
        await foreach (string line in _lines.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            try
            {
                Directory.CreateDirectory(directory);
                if (File.Exists(path) && new FileInfo(path).Length >= 1_048_576)
                    File.Move(path, Path.Combine(directory, "ime-input.previous.jsonl"), true);
                await File.AppendAllTextAsync(path, line + Environment.NewLine).ConfigureAwait(false);
            }
            catch (Exception) { /* Diagnostics cannot block or fail input handling. */ }
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0) _lines.Writer.TryComplete();
    }
}
