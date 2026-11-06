using System.Text;
using System.Text.Json;
using PinyinTranslate.Update;
using Velopack;
using Velopack.Logging;
using Velopack.Sources;

namespace PinyinTranslate.Update.Tests;

// The adapter runs against fake SDK sessions or the real GitHub source with a
// pure in-memory downloader. No TestVelopackLocator/Run/Process/GUI is invoked.
public sealed class VelopackBackendTests
{
    [Fact]
    public void ConstructionDoesNotInvokeTheSdkFactory()
    {
        int created = 0;
        _ = new VelopackBackend(() => { created++; return new Session(); });
        _ = new VelopackBackend();
        Assert.Equal(0, created);
    }

    [Fact]
    public async Task UnmanagedSessionCannotCheckDownloadOrPrepare()
    {
        var session = new Session { IsManaged = false };
        var backend = new VelopackBackend(() => session);
        Assert.Null(await backend.CheckAsync(default));
        await Assert.ThrowsAsync<InvalidDataException>(() => backend.DownloadAsync(new("1.0.1"), new Progress(), default));
        Assert.Throws<InvalidDataException>(() => backend.PrepareRestart(new("1.0.1")));
        Assert.Equal(0, session.Checks + session.Downloads + session.Handoffs);
    }

    [Fact]
    public async Task ValidOwnedReleaseIsHandedOffOnlyOnceAfterSdkDownloadSucceeds()
    {
        var session = new Session();
        var backend = new VelopackBackend(() => session);
        var release = await backend.CheckAsync(default);
        Assert.NotNull(release); Assert.Equal("1.0.1", release.Version); Assert.Equal(4096, release.DownloadBytes);
        Assert.Throws<InvalidOperationException>(() => backend.PrepareRestart(release));
        await backend.DownloadAsync(release, new Progress(), default);
        backend.PrepareRestart(release);
        Assert.Throws<InvalidOperationException>(() => backend.PrepareRestart(release));
        Assert.Equal(1, session.Downloads); Assert.Equal(1, session.Handoffs);
    }

    [Fact]
    public async Task FailedSdkHashVerificationNeverArmsAnUpdaterTicket()
    {
        var session = new Session { DownloadError = new InvalidDataException("hash mismatch") };
        var backend = new VelopackBackend(() => session);
        var release = (await backend.CheckAsync(default))!;
        await Assert.ThrowsAsync<InvalidDataException>(() => backend.DownloadAsync(release, new Progress(), default));
        Assert.Throws<InvalidOperationException>(() => backend.PrepareRestart(release));
        Assert.Equal(0, session.Handoffs);
    }

    [Fact]
    public async Task CopiedOrForeignDisplayMetadataCannotBecomeAnUpdateTicket()
    {
        var backend = new VelopackBackend(() => new Session());
        var release = (await backend.CheckAsync(default))!;
        await Assert.ThrowsAsync<InvalidDataException>(() => backend.DownloadAsync(release with { }, new Progress(), default));
        var another = new VelopackBackend(() => new Session());
        await Assert.ThrowsAsync<InvalidDataException>(() => another.DownloadAsync(release, new Progress(), default));
    }

    [Fact]
    public async Task ANewCheckRevokesThePreviouslyCheckedTicket()
    {
        var backend = new VelopackBackend(() => new Session());
        var old = (await backend.CheckAsync(default))!;
        var latest = (await backend.CheckAsync(default))!;
        await Assert.ThrowsAsync<InvalidDataException>(() => backend.DownloadAsync(old, new Progress(), default));
        await backend.DownloadAsync(latest, new Progress(), default);
        backend.PrepareRestart(latest);
    }

    [Theory]
    [InlineData("package")]
    [InlineData("channel")]
    [InlineData("prerelease")]
    [InlineData("hash")]
    [InlineData("path")]
    [InlineData("size")]
    [InlineData("type")]
    [InlineData("four-part")]
    public async Task InvalidAssetMetadataIsRejectedBeforeDownloadOrHandoff(string mutation)
    {
        var asset = Asset();
        switch (mutation)
        {
            case "package": asset.PackageId = "AnotherApp"; break;
            case "channel": asset.FileName = asset.FileName.Replace("win-x64", "win-arm64"); break;
            case "prerelease": asset.Version = SemanticVersion.Parse("1.0.1-beta.1"); break;
            case "hash": asset.SHA256 = "not-a-checksum"; break;
            case "path": asset.FileName = "../" + asset.FileName; break;
            case "size": asset.Size = 0; break;
            case "type": asset.Type = VelopackAssetType.Portable; break;
            case "four-part": asset.Version = SemanticVersion.Parse("1.0.1.4"); break;
        }
        var session = new Session { Info = new(asset, false) };
        var backend = new VelopackBackend(() => session);
        await Assert.ThrowsAsync<InvalidDataException>(() => backend.CheckAsync(default));
        Assert.Equal(0, session.Downloads + session.Handoffs);
    }

    [Theory]
    [InlineData("1.0.0")]
    [InlineData("0.9.0")]
    public async Task EqualOrOlderVersionsCannotBeInstalledEvenIfTheSessionOffersThem(string version)
    {
        var session = new Session { Info = new(Asset(version), false) };
        var backend = new VelopackBackend(() => session);
        await Assert.ThrowsAsync<InvalidDataException>(() => backend.CheckAsync(default));
    }

    [Theory]
    [InlineData("package")]
    [InlineData("channel")]
    public async Task WrongManagedInstallationIdentityIsRejectedBeforeChecking(string mutation)
    {
        var session = new Session();
        if (mutation == "package") session.AppId = "OtherApp"; else session.Channel = "beta";
        var backend = new VelopackBackend(() => session);
        await Assert.ThrowsAsync<InvalidDataException>(() => backend.CheckAsync(default));
        Assert.Equal(0, session.Checks);
    }

    [Fact]
    public async Task AssetMetadataCannotBeChangedBetweenCheckDownloadAndHandoff()
    {
        var session = new Session();
        var backend = new VelopackBackend(() => session);
        var release = (await backend.CheckAsync(default))!;
        await backend.DownloadAsync(release, new Progress(), default);
        session.Info!.TargetFullRelease.SHA256 = new string('B', 64);
        Assert.Throws<InvalidDataException>(() => backend.PrepareRestart(release));
        Assert.Equal(0, session.Handoffs);
    }

    [Fact]
    public async Task CancelledLateSessionResultCannotReplaceTheLatestTicket()
    {
        var late = new TaskCompletionSource<UpdateInfo?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var session = new Session { Check = _ => late.Task };
        var backend = new VelopackBackend(() => session);
        using var cancellation = new CancellationTokenSource();
        var old = backend.CheckAsync(cancellation.Token);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => old);
        session.Check = _ => Task.FromResult<UpdateInfo?>(new(Asset("1.0.2"), false));
        var latest = (await backend.CheckAsync(default))!;
        late.SetResult(new(Asset("1.0.1"), false));
        await backend.DownloadAsync(latest, new Progress(), default);
        backend.PrepareRestart(latest);
        Assert.Equal("1.0.2", session.Prepared!.TargetFullRelease.Version.ToFullString());
    }

    [Fact]
    public async Task LatestGithubSourceReadsOneAnonymousStableFeedWithoutListingHistoricalReleases()
    {
        var downloader = new Downloader(Feed(Asset()));
        var source = VelopackBackend.CreateSource(downloader);
        var feed = await source.GetReleaseFeed(new NullVelopackLogger(), VelopackBackend.PackageId, VelopackBackend.Channel);
        Assert.Single(feed.Assets);
        Assert.Equal(VelopackBackend.PackageId, feed.Assets[0].PackageId);
        Assert.Equal(LatestGithubUpdateSource.FeedUrl, Assert.Single(downloader.Requests).Url);
        Assert.All(downloader.Requests, request => Assert.False(request.HasAuthorization));
        Assert.Equal(0, downloader.FileDownloads);
    }

    [Fact]
    public async Task RealGithubSourceRejectsAWrongFeedAndDoesNotTurnItIntoNoUpdates()
    {
        var asset = Asset(); asset.PackageId = "WrongApplication";
        var source = VelopackBackend.CreateSource(new Downloader(Feed(asset)));
        await Assert.ThrowsAsync<InvalidDataException>(() => source.GetReleaseFeed(new NullVelopackLogger(),
            VelopackBackend.PackageId, VelopackBackend.Channel));
    }

    [Fact]
    public async Task MissingChannelFeedIsReportedAsUnavailable()
    {
        var source = VelopackBackend.CreateSource(new Downloader(Feed(Asset())) { IncludeFeed = false });
        await Assert.ThrowsAsync<UpdateFeedUnavailableException>(() => source.GetReleaseFeed(new NullVelopackLogger(),
            VelopackBackend.PackageId, VelopackBackend.Channel));
    }

    [Fact]
    public void WritePreflightChecksAllThreeExistingDirectoriesAndRemovesOnlyItsCreatedFiles()
    {
        var access = new DirectoryAccess();
        UpdateDirectoryWritePreflight.EnsureWritable(Directories, access, () => ".probe.tmp");
        Assert.Equal(Directories.Select(path => Path.Combine(path, ".probe.tmp")), access.Created);
        Assert.Equal(access.Created, access.Deleted);
        Assert.Empty(access.Files);
        Assert.Equal(3, access.Writes);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void WritePreflightStopsOnAnyDirectoryPermissionFailure(int denied)
    {
        var access = new DirectoryAccess { DeniedDirectory = Directories[denied] };
        Assert.Throws<UnauthorizedAccessException>(() => UpdateDirectoryWritePreflight.EnsureWritable(Directories, access, () => ".probe.tmp"));
        Assert.Equal(denied, access.Created.Count);
        Assert.Equal(access.Created, access.Deleted);
        Assert.Empty(access.Files);
    }

    [Fact]
    public void CreateNewCollisionNeverDeletesAnExistingFile()
    {
        var access = new DirectoryAccess();
        string existing = Path.Combine(Directories[0], ".probe.tmp");
        access.Files.Add(existing);
        Assert.Throws<IOException>(() => UpdateDirectoryWritePreflight.EnsureWritable(Directories, access, () => ".probe.tmp"));
        Assert.Empty(access.Created); Assert.Empty(access.Deleted);
        Assert.Contains(existing, access.Files);
    }

    [Theory]
    [InlineData("write")]
    [InlineData("flush")]
    [InlineData("dispose")]
    public void StreamFailureCleansItsOwnNewFileAndCannotPassPreflight(string stage)
    {
        var access = new DirectoryAccess { FailWrite = stage == "write", FailFlush = stage == "flush", FailDispose = stage == "dispose" };
        Assert.Throws<IOException>(() => UpdateDirectoryWritePreflight.EnsureWritable(Directories, access, () => ".probe.tmp"));
        Assert.Single(access.Created); Assert.Equal(access.Created, access.Deleted);
        Assert.Empty(access.Files);
    }

    [Fact]
    public void DeletePermissionFailureCannotPassPreflight()
    {
        var access = new DirectoryAccess { FailDelete = true };
        Assert.Throws<UnauthorizedAccessException>(() => UpdateDirectoryWritePreflight.EnsureWritable(Directories, access, () => ".probe.tmp"));
        Assert.Single(access.Created); Assert.Single(access.Deleted);
    }

    [Fact]
    public void MissingDirectoryIsNotCreatedByPreflight()
    {
        var access = new DirectoryAccess { MissingDirectory = Directories[1] };
        Assert.Throws<IOException>(() => UpdateDirectoryWritePreflight.EnsureWritable(Directories, access, () => ".probe.tmp"));
        Assert.Single(access.Created); Assert.Empty(access.Files);
    }

    private static readonly string[] Directories = [@"C:\fake-portable", @"C:\fake-portable\current", @"C:\fake-portable\packages"];
    private sealed class DirectoryAccess : IUpdateDirectoryAccess
    {
        public string? DeniedDirectory, MissingDirectory;
        public bool FailWrite, FailFlush, FailDispose, FailDelete;
        public int Writes;
        public HashSet<string> Files = new(StringComparer.OrdinalIgnoreCase);
        public List<string> Created = [], Deleted = [];
        public bool DirectoryExists(string path) => path != MissingDirectory;
        public Stream CreateNew(string path)
        {
            if (Path.GetDirectoryName(path) == DeniedDirectory) throw new UnauthorizedAccessException();
            if (!Files.Add(path)) throw new IOException("Already exists");
            Created.Add(path);
            return new ProbeStream(this);
        }
        public void Delete(string path)
        {
            Deleted.Add(path);
            if (FailDelete) throw new UnauthorizedAccessException();
            Files.Remove(path);
        }
        private sealed class ProbeStream(DirectoryAccess owner) : MemoryStream
        {
            public override void WriteByte(byte value)
            {
                if (owner.FailWrite) throw new IOException("Write denied");
                owner.Writes++; base.WriteByte(value);
            }
            public override void Flush()
            {
                if (owner.FailFlush) throw new IOException("Flush denied");
                base.Flush();
            }
            protected override void Dispose(bool disposing)
            {
                base.Dispose(disposing);
                if (owner.FailDispose) throw new IOException("Close failed");
            }
        }
    }

    private static VelopackAsset Asset(string version = "1.0.1") => new()
    {
        PackageId = VelopackBackend.PackageId, Version = SemanticVersion.Parse(version), Type = VelopackAssetType.Full,
        FileName = $"{VelopackBackend.PackageId}-{version}-{VelopackBackend.Channel}-full.nupkg",
        Size = 4096, SHA256 = new string('A', 64), SHA1 = new string('1', 40), NotesMarkdown = "Release notes"
    };
    private static string Feed(VelopackAsset asset) => JsonSerializer.Serialize(new
    {
        Assets = new[] { new { asset.PackageId, Version = asset.Version.ToFullString(), Type = "Full", asset.FileName,
            asset.Size, asset.SHA256, asset.SHA1, asset.NotesMarkdown } }
    });
    private sealed class Progress : IProgress<int> { public void Report(int value) { } }
    private sealed class Session : IVelopackUpdateSession
    {
        public bool IsManaged { get; set; } = true;
        public string CurrentVersion => "1.0.0";
        public string AppId { get; set; } = VelopackBackend.PackageId;
        public string Channel { get; set; } = VelopackBackend.Channel;
        public UpdateInfo? Info = new(Asset(), false), Prepared;
        public Func<CancellationToken, Task<UpdateInfo?>>? Check;
        public Exception? DownloadError;
        public int Checks, Downloads, Handoffs;
        public Task<UpdateInfo?> CheckAsync(CancellationToken cancellationToken) { Checks++; return Check?.Invoke(cancellationToken) ?? Task.FromResult(Info); }
        public Task DownloadAsync(UpdateInfo info, Action<int> progress, CancellationToken cancellationToken)
        { Downloads++; return DownloadError is null ? Task.CompletedTask : Task.FromException(DownloadError); }
        public void PrepareRestart(UpdateInfo info) { Handoffs++; Prepared = info; }
    }
    private sealed class Downloader(string feed) : IFileDownloader
    {
        public bool IncludeFeed = true;
        public int FileDownloads;
        public List<(string Url, bool HasAuthorization)> Requests = [];
        private void Record(string url, IDictionary<string, string>? headers) =>
            Requests.Add((url, headers?.Keys.Any(key => key.Equals("Authorization", StringComparison.OrdinalIgnoreCase)) == true));
        public Task<string> DownloadString(string url, IDictionary<string, string>? headers, double timeout)
        {
            Record(url, headers);
            return Task.FromResult(JsonSerializer.Serialize(new[]
            {
                new { name = "v1.0.1", prerelease = false, published_at = "2026-11-06T00:00:00Z",
                    assets = IncludeFeed ? new[] { new { name = "releases.win-x64.json",
                        url = "https://api.github.com/repos/ihhhhhooo/PinyinTranslate/releases/assets/1",
                        browser_download_url = "https://github.com/ihhhhhooo/PinyinTranslate/releases/download/v1.0.1/releases.win-x64.json" } } : [] }
            }));
        }
        public Task<byte[]> DownloadBytes(string url, IDictionary<string, string>? headers, double timeout)
        {
            Record(url, headers);
            if (!IncludeFeed) throw new HttpRequestException("Not found", null, System.Net.HttpStatusCode.NotFound);
            return Task.FromResult(Encoding.UTF8.GetBytes(feed));
        }
        public Task DownloadFile(string url, string targetFile, Action<int> progress, IDictionary<string, string>? headers,
            double timeout, CancellationToken cancelToken = default)
        { FileDownloads++; throw new InvalidOperationException("No real or fake disk download is permitted in this feed test."); }
    }
}
