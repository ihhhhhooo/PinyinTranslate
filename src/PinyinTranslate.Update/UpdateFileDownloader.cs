using System.Buffers;
using System.Net;
using System.Net.Http.Headers;
using Velopack.Sources;

namespace PinyinTranslate.Update;

/// <summary>Pooled HTTPS connections and bounded, validator-protected retries within a download.</summary>
internal sealed class UpdateFileDownloader : IFileDownloader
{
    private static readonly HttpClient SharedClient = new(new SocketsHttpHandler
    {
        AllowAutoRedirect = true, MaxAutomaticRedirections = 10,
        AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
        PooledConnectionLifetime = TimeSpan.FromMinutes(10), ConnectTimeout = TimeSpan.FromSeconds(10)
    }) { Timeout = Timeout.InfiniteTimeSpan, MaxResponseContentBufferSize = 8 * 1024 * 1024,
        DefaultRequestVersion = HttpVersion.Version20, DefaultVersionPolicy = HttpVersionPolicy.RequestVersionOrLower };
    private readonly HttpClient http;
    private readonly TimeSpan readStallTimeout;
    internal UpdateFileDownloader() : this(SharedClient) { }
    internal UpdateFileDownloader(HttpClient http, TimeSpan? readStallTimeout = null)
    {
        ArgumentNullException.ThrowIfNull(http);
        this.http = http;
        this.readStallTimeout = readStallTimeout ?? TimeSpan.FromSeconds(30);
        if (this.readStallTimeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(readStallTimeout));
    }

    public async Task<byte[]> DownloadBytes(string url, IDictionary<string, string>? headers, double timeout)
    {
        using var deadline = new CancellationTokenSource(RequestTimeout(timeout, TimeSpan.FromSeconds(15)));
        try
        {
            using var request = Request(url, headers);
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseContentRead, deadline.Token).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadAsByteArrayAsync(deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException error) when (deadline.IsCancellationRequested)
        { throw new TimeoutException("UpdateMetadataTimeout", error); }
    }

    public async Task<string> DownloadString(string url, IDictionary<string, string>? headers, double timeout) =>
        System.Text.Encoding.UTF8.GetString(await DownloadBytes(url, headers, timeout).ConfigureAwait(false));

    public async Task DownloadFile(string url, string targetFile, Action<int> progress, IDictionary<string, string>? headers,
        double timeout, CancellationToken cancelToken = default)
    {
        cancelToken.ThrowIfCancellationRequested();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancelToken);
        deadline.CancelAfter(RequestTimeout(timeout, TimeSpan.FromMinutes(30)));
        var token = deadline.Token;
        await using var file = new FileStream(targetFile, FileMode.Create, FileAccess.Write, FileShare.None, 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        long? expectedLength = null;
        EntityTagHeaderValue? validator = null;
        int lastProgress = -1;
        byte[] buffer = ArrayPool<byte>.Shared.Rent(128 * 1024);
        try
        {
            for (int attempt = 0; ; attempt++)
            {
                try
                {
                    token.ThrowIfCancellationRequested();
                    // Without a strong validator we cannot safely join two responses.
                    if (file.Position > 0 && validator is null) { file.SetLength(0); file.Position = 0; expectedLength = null; }
                    long offset = file.Position;
                    using var request = Request(url, headers);
                    request.Headers.AcceptEncoding.Add(new StringWithQualityHeaderValue("identity"));
                    if (offset > 0)
                    {
                        request.Headers.Range = new RangeHeaderValue(offset, null);
                        request.Headers.IfRange = new RangeConditionHeaderValue(validator!);
                    }
                    using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
                    response.EnsureSuccessStatusCode();
                    if (response.StatusCode == HttpStatusCode.PartialContent)
                    {
                        var range = response.Content.Headers.ContentRange;
                        if (offset == 0 || range?.Unit != "bytes" || range.From != offset || !range.Length.HasValue ||
                            range.To != range.Length - 1 || range.Length != expectedLength ||
                            (response.Content.Headers.ContentLength is { } remaining && remaining != range.Length - offset) ||
                            response.Headers.ETag is not { IsWeak: false } tag || !tag.Equals(validator))
                            throw new InvalidDataException("InvalidUpdateResumeResponse");
                    }
                    else if (response.StatusCode == HttpStatusCode.OK)
                    {
                        // If-Range mismatch / unsupported Range: restart with the complete response.
                        file.SetLength(0); file.Position = 0;
                        expectedLength = response.Content.Headers.ContentLength;
                        validator = response.Headers.ETag is { IsWeak: false } tag ? tag : null;
                    }
                    else throw new InvalidDataException("UnexpectedUpdateDownloadResponse");

                    await using var input = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
                    while (expectedLength is null || file.Position < expectedLength)
                    {
                        int count;
                        using var readDeadline = CancellationTokenSource.CreateLinkedTokenSource(token);
                        readDeadline.CancelAfter(readStallTimeout);
                        try { count = await input.ReadAsync(buffer.AsMemory(), readDeadline.Token).ConfigureAwait(false); }
                        catch (OperationCanceledException error) when (!token.IsCancellationRequested)
                        { throw new HttpRequestException("UpdateDownloadStalled", new TimeoutException("UpdateReadTimeout", error)); }
                        catch (IOException error) { throw new HttpRequestException("UpdateDownloadInterrupted", error); }
                        if (count == 0) break;
                        if (expectedLength is { } total && file.Position + count > total) throw new InvalidDataException("UpdateDownloadTooLong");
                        await file.WriteAsync(buffer.AsMemory(0, count), token).ConfigureAwait(false);
                        if (expectedLength is > 0)
                        {
                            // SDK rounds odd percentages upward to even numbers:
                            // reserve 100 for flushed EOF instead of rounding 99 to 100 early.
                            int percent = (int)Math.Min(98, file.Position * 100 / expectedLength.Value);
                            if (percent > lastProgress) { lastProgress = percent; progress(percent); }
                        }
                    }
                    if (expectedLength is { } length && file.Position != length) throw new HttpRequestException("UpdateDownloadIncomplete");
                    await file.FlushAsync(token).ConfigureAwait(false);
                    progress(100); // SDK still verifies the complete package hash before allowing installation.
                    return;
                }
                catch (HttpRequestException error) when (attempt < 2 && !token.IsCancellationRequested && IsTransient(error))
                { await Task.Delay(TimeSpan.FromMilliseconds(200 * (attempt + 1)), token).ConfigureAwait(false); }
            }
        }
        catch (OperationCanceledException error) when (!cancelToken.IsCancellationRequested && deadline.IsCancellationRequested)
        { throw new TimeoutException("UpdateDownloadTimeout", error); }
        finally { ArrayPool<byte>.Shared.Return(buffer); }
    }

    private static bool IsTransient(HttpRequestException error) => error.StatusCode is null or HttpStatusCode.RequestTimeout ||
        (int)error.StatusCode.Value >= 500;

    private static TimeSpan RequestTimeout(double minutes, TimeSpan ceiling) =>
        minutes > 0 && double.IsFinite(minutes) ? TimeSpan.FromMinutes(Math.Min(minutes, ceiling.TotalMinutes)) : ceiling;

    private static HttpRequestMessage Request(string url, IDictionary<string, string>? headers)
    {
        var uri = new Uri(url, UriKind.Absolute);
        if (uri.Scheme != Uri.UriSchemeHttps) throw new InvalidDataException("UpdateRequiresHttps");
        var request = new HttpRequestMessage(HttpMethod.Get, uri)
        { Version = HttpVersion.Version20, VersionPolicy = HttpVersionPolicy.RequestVersionOrLower };
        request.Headers.UserAgent.ParseAdd("PinyinTranslate-Updater/1.0");
        foreach (var header in headers ?? new Dictionary<string, string>()) request.Headers.TryAddWithoutValidation(header.Key, header.Value);
        return request;
    }
}
