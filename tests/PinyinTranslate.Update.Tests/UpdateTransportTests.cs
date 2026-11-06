using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using PinyinTranslate.Update;
using Velopack;
using Velopack.Logging;

namespace PinyinTranslate.Update.Tests;

// All HTTP is injected in-memory; files are random temporary synthetic packages.
// No real locator, UpdateManager, network, user data, process or desktop is used.
public sealed class UpdateTransportTests
{
    private const string Url = "https://updates.invalid/Package-1.0.5.nupkg";
    private static readonly byte[] Payload = Encoding.UTF8.GetBytes("synthetic package contents");

    [Fact]
    public async Task SourceUsesOneFeedAndPinsThePackageUrlEvenIfLatestChanges()
    {
        var asset = Asset();
        int metadataRequests = 0;
        string? downloadedUrl = null;
        using var fixture = new Fixture((request, _) =>
        {
            if (request.RequestUri!.AbsoluteUri == LatestGithubUpdateSource.FeedUrl)
            {
                metadataRequests++;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                { Content = new StringContent(JsonSerializer.Serialize(new { Assets = new[] { new
                    { asset.PackageId, Version = asset.Version.ToFullString(), Type = "Full", asset.FileName, asset.Size, asset.SHA256, asset.SHA1 }
                } })) });
            }
            downloadedUrl = request.RequestUri.AbsoluteUri;
            return Task.FromResult(Response(Payload));
        });
        var source = VelopackBackend.CreateSource(fixture.Downloader);
        var feed = await source.GetReleaseFeed(new NullVelopackLogger(), VelopackBackend.PackageId, VelopackBackend.Channel);
        Assert.Single(feed.Assets);
        asset.Version = SemanticVersion.Parse("1.0.6"); // Only the parsed/check-time version may be downloaded.
        await source.DownloadReleaseEntry(new NullVelopackLogger(), feed.Assets[0], fixture.Path, _ => { }, default);
        Assert.Equal(1, metadataRequests);
        Assert.Equal(VelopackBackend.RepositoryUrl + "/releases/download/v1.0.5/PinyinTranslate.Desktop-1.0.5-win-x64-full.nupkg", downloadedUrl);
        Assert.Equal(Payload, await File.ReadAllBytesAsync(fixture.Path));
    }

    [Theory]
    [InlineData("wrong-id", "win-x64")]
    [InlineData("PinyinTranslate.Desktop", "wrong-channel")]
    public async Task WrongSourceIdentityNeverMakesAnHttpRequest(string id, string channel)
    {
        using var fixture = new Fixture((_, _) => throw new Exception("HTTP must not be reached"));
        var source = VelopackBackend.CreateSource(fixture.Downloader);
        await Assert.ThrowsAsync<InvalidDataException>(() => source.GetReleaseFeed(new NullVelopackLogger(), id, channel));
        Assert.Equal(0, fixture.Requests);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{\"Assets\":[]}")]
    public async Task AFailedFeedCannotBeReportedAsNoNewUpdates(string feed)
    {
        using var fixture = new Fixture((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(feed) }));
        var source = VelopackBackend.CreateSource(fixture.Downloader);
        await Assert.ThrowsAnyAsync<Exception>(() => source.GetReleaseFeed(new NullVelopackLogger(), VelopackBackend.PackageId, VelopackBackend.Channel));
        Assert.Equal(1, fixture.Requests);
    }

    [Fact]
    public async Task MetadataTimeoutIsBoundedAndDoesNotRetryALowercasedUrl()
    {
        using var fixture = new Fixture(async (_, token) => { await Task.Delay(Timeout.Infinite, token); throw new Exception("Unreachable"); });
        await Assert.ThrowsAsync<TimeoutException>(() => fixture.Downloader.DownloadBytes(Url, null, 0.001));
        Assert.Equal(1, fixture.Requests);
    }

    [Fact]
    public async Task InterruptedDownloadResumesOnlyWithAStrongMatchingValidator()
    {
        using var fixture = new Fixture((request, count) =>
        {
            Assert.Contains(request.Headers.AcceptEncoding, x => x.Value == "identity");
            if (count == 1) return Task.FromResult(InterruptedResponse());
            Assert.Equal("bytes=4-", request.Headers.Range!.ToString());
            Assert.Equal("\"original\"", request.Headers.IfRange!.ToString());
            return Task.FromResult(RangeResponse(4));
        });
        var progress = new List<int>();
        await fixture.Downloader.DownloadFile(Url, fixture.Path, progress.Add, null, 1);
        Assert.Equal(Payload, await File.ReadAllBytesAsync(fixture.Path));
        Assert.Equal(2, fixture.Requests);
        Assert.Equal(100, progress[^1]);
        Assert.Equal(progress.OrderBy(x => x), progress);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AbsentOrWeakValidatorsRestartInsteadOfJoiningUnrelatedResponses(bool weak)
    {
        using var fixture = new Fixture((request, count) =>
        {
            if (count == 1)
            {
                var response = InterruptedResponse();
                response.Headers.ETag = weak ? new EntityTagHeaderValue("\"original\"", true) : null;
                return Task.FromResult(response);
            }
            Assert.Null(request.Headers.Range);
            Assert.Null(request.Headers.IfRange);
            return Task.FromResult(Response(Payload));
        });
        await fixture.Downloader.DownloadFile(Url, fixture.Path, _ => { }, null, 1);
        Assert.Equal(Payload, await File.ReadAllBytesAsync(fixture.Path));
        Assert.Equal(2, fixture.Requests);
    }

    [Fact]
    public async Task IgnoredRangeOrChangedEntityRestartsUsingTheFullResponse()
    {
        byte[] changed = Encoding.UTF8.GetBytes("a replacement server response");
        using var fixture = new Fixture((request, count) =>
        {
            if (count == 1) return Task.FromResult(InterruptedResponse());
            Assert.NotNull(request.Headers.Range);
            var response = Response(changed);
            response.Headers.ETag = new EntityTagHeaderValue("\"changed\"");
            return Task.FromResult(response);
        });
        await fixture.Downloader.DownloadFile(Url, fixture.Path, _ => { }, null, 1);
        Assert.Equal(changed, await File.ReadAllBytesAsync(fixture.Path));
    }

    [Theory]
    [InlineData("offset")]
    [InlineData("length")]
    [InlineData("validator")]
    public async Task InvalidPartialResponsesAreRejectedWithoutClaimingCompletion(string mutation)
    {
        using var fixture = new Fixture((_, count) =>
        {
            if (count == 1) return Task.FromResult(InterruptedResponse());
            var response = RangeResponse(4);
            if (mutation == "offset") response.Content.Headers.ContentRange = new ContentRangeHeaderValue(5, Payload.Length - 1, Payload.Length);
            if (mutation == "length") response.Content.Headers.ContentLength = Payload.Length;
            if (mutation == "validator") response.Headers.ETag = new EntityTagHeaderValue("\"changed\"");
            return Task.FromResult(response);
        });
        int progress = 0;
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Downloader.DownloadFile(Url, fixture.Path, value => progress = value, null, 1));
        Assert.True(progress < 100);
        Assert.Equal(2, fixture.Requests);
    }

    [Theory]
    [InlineData(400)]
    [InlineData(401)]
    [InlineData(403)]
    [InlineData(404)]
    [InlineData(429)]
    public async Task PermanentHttpErrorsFailWithoutRepeatedRequests(int status)
    {
        using var fixture = new Fixture((_, _) => Task.FromResult(new HttpResponseMessage((HttpStatusCode)status)));
        await Assert.ThrowsAsync<HttpRequestException>(() => fixture.Downloader.DownloadFile(Url, fixture.Path, _ => { }, null, 1));
        Assert.Equal(1, fixture.Requests);
    }

    [Fact]
    public async Task ATransientServerFailureCanRetryWithoutChangingTheCaseOfTheUrl()
    {
        using var fixture = new Fixture((request, count) =>
        {
            Assert.Equal(Url, request.RequestUri!.AbsoluteUri);
            return Task.FromResult(count == 1 ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) : Response(Payload));
        });
        await fixture.Downloader.DownloadFile(Url, fixture.Path, _ => { }, null, 1);
        Assert.Equal(2, fixture.Requests);
        Assert.Equal(Payload, await File.ReadAllBytesAsync(fixture.Path));
    }

    [Fact]
    public async Task TruncatedBodiesHaveBoundedRetriesAndNeverReportComplete()
    {
        using var fixture = new Fixture((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new StreamContent(new MemoryStream(Payload[..4])) { Headers = { ContentLength = Payload.Length } } }));
        var progress = new List<int>();
        await Assert.ThrowsAsync<HttpRequestException>(() => fixture.Downloader.DownloadFile(Url, fixture.Path, progress.Add, null, 1));
        Assert.Equal(3, fixture.Requests);
        Assert.DoesNotContain(100, progress);
    }

    [Fact]
    public async Task ExplicitCancellationStopsABodyReadWithoutRetryingOrReportingComplete()
    {
        var reading = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellation = new CancellationTokenSource();
        using var fixture = new Fixture((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new StreamContent(new WaitingStream(reading)) { Headers = { ContentLength = Payload.Length } } }));
        var progress = new List<int>();
        Task download = fixture.Downloader.DownloadFile(Url, fixture.Path, progress.Add, null, 1, cancellation.Token);
        await reading.Task.WaitAsync(TimeSpan.FromSeconds(3));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => download.WaitAsync(TimeSpan.FromSeconds(3)));
        Assert.Equal(1, fixture.Requests);
        Assert.DoesNotContain(100, progress);
    }

    [Fact]
    public async Task AStalledBodyReadHasABoundedWaitAndRetryCount()
    {
        using var fixture = new Fixture((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new StreamContent(new WaitingStream(new())) { Headers = { ContentLength = Payload.Length } } }), TimeSpan.FromMilliseconds(20));
        await Assert.ThrowsAsync<HttpRequestException>(() => fixture.Downloader.DownloadFile(Url, fixture.Path, _ => { }, null, 1).WaitAsync(TimeSpan.FromSeconds(3)));
        Assert.Equal(3, fixture.Requests);
    }

    [Fact]
    public async Task SdkProgressRoundingCannotStartVerificationWhileTheFinalByteIsStillWaiting()
    {
        var releaseTail = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstProgress = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var fixture = new Fixture((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new StreamContent(new WaitingTailStream(releaseTail)) { Headers = { ContentLength = 200 } } }));
        int last = 0;
        Task download = fixture.Downloader.DownloadFile(Url, fixture.Path, percent =>
        {
            last = (int)(Math.Round(percent / 2d, MidpointRounding.AwayFromZero) * 2);
            firstProgress.TrySetResult(last);
        }, null, 1);
        try
        {
            Assert.True(await firstProgress.Task.WaitAsync(TimeSpan.FromSeconds(3)) < 100);
            Assert.False(download.IsCompleted);
            releaseTail.TrySetResult();
            await download.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.Equal(100, last);
        }
        finally { releaseTail.TrySetResult(); }
    }

    private static HttpResponseMessage Response(byte[] data) => new(HttpStatusCode.OK) { Content = new ByteArrayContent(data) };
    private static HttpResponseMessage InterruptedResponse()
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new StreamContent(new InterruptedStream()) { Headers = { ContentLength = Payload.Length } } };
        response.Headers.ETag = new EntityTagHeaderValue("\"original\"");
        return response;
    }
    private static HttpResponseMessage RangeResponse(int offset)
    {
        var response = new HttpResponseMessage(HttpStatusCode.PartialContent) { Content = new ByteArrayContent(Payload[offset..]) };
        response.Content.Headers.ContentRange = new ContentRangeHeaderValue(offset, Payload.Length - 1, Payload.Length);
        response.Headers.ETag = new EntityTagHeaderValue("\"original\"");
        return response;
    }
    private static VelopackAsset Asset() => new()
    {
        PackageId = VelopackBackend.PackageId, Version = SemanticVersion.Parse("1.0.5"), Type = VelopackAssetType.Full,
        FileName = "PinyinTranslate.Desktop-1.0.5-win-x64-full.nupkg", Size = Payload.Length, SHA256 = new string('A', 64), SHA1 = new string('1', 40)
    };
    private sealed class Fixture : IDisposable
    {
        private static readonly string Root = System.IO.Path.GetFullPath(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "PinyinTranslate-update-transport-tests"));
        private readonly string directory = System.IO.Path.Combine(Root, Guid.NewGuid().ToString("N"));
        private readonly HttpClient http;
        public string Path => System.IO.Path.Combine(directory, "synthetic.partial");
        public UpdateFileDownloader Downloader { get; }
        public int Requests;
        public Fixture(Func<HttpRequestMessage, int, Task<HttpResponseMessage>> response, TimeSpan? readTimeout = null) :
            this((request, _) => response(request, 0), readTimeout, response) { }
        public Fixture(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> response) : this(response, null, null) { }
        private Fixture(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> response, TimeSpan? readTimeout,
            Func<HttpRequestMessage, int, Task<HttpResponseMessage>>? counted)
        {
            Directory.CreateDirectory(directory);
            http = new(new Handler((request, token) =>
            {
                int count = Interlocked.Increment(ref Requests);
                Assert.Null(request.Headers.Authorization);
                return counted is null ? response(request, token) : counted(request, count);
            }));
            Downloader = new(http, readTimeout);
        }
        public void Dispose()
        {
            http.Dispose();
            string resolved = System.IO.Path.GetFullPath(directory);
            if (System.IO.Path.GetDirectoryName(resolved) == Root && Directory.Exists(resolved)) Directory.Delete(resolved, true);
        }
    }
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> response) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => response(request, cancellationToken); }
    private sealed class InterruptedStream : MemoryStream
    {
        public InterruptedStream() : base(Payload) { }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (Position >= 4) throw new IOException("Synthetic interrupted response");
            return base.ReadAsync(buffer[..Math.Min(buffer.Length, (int)(4 - Position))], cancellationToken);
        }
    }
    private sealed class WaitingStream(TaskCompletionSource reading) : MemoryStream
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        { reading.TrySetResult(); await Task.Delay(Timeout.Infinite, cancellationToken); return 0; }
    }
    private sealed class WaitingTailStream(TaskCompletionSource release) : MemoryStream(new byte[200])
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (Position == 0) return await base.ReadAsync(buffer[..199], cancellationToken);
            await release.Task.WaitAsync(cancellationToken);
            return await base.ReadAsync(buffer, cancellationToken);
        }
    }
}
