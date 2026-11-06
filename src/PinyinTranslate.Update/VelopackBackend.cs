using System.Reflection;
using System.Text.RegularExpressions;
using Velopack;
using Velopack.Locators;
using Velopack.Logging;
using Velopack.Sources;

namespace PinyinTranslate.Update;

/// <summary>Public GitHub feed plus the official Velopack download/verification/update engine.</summary>
public sealed class VelopackBackend : IUpdateBackend
{
    public const string RepositoryUrl = "https://github.com/ihhhhhooo/trans";
    public const string PackageId = "PinyinTranslate.Desktop";
    public const string Channel = "win-x64";
    private readonly object gate = new();
    private readonly Func<IVelopackUpdateSession> sessionFactory;
    private IVelopackUpdateSession? session;
    private Ticket? current;
    private long generation;
    private sealed class Ticket(VelopackBackend owner, IVelopackUpdateSession session, UpdateInfo info)
    {
        public VelopackBackend Owner { get; } = owner;
        public IVelopackUpdateSession Session { get; } = session;
        public UpdateInfo Info { get; } = info;
        public AssetIdentity Identity { get; } = AssetIdentity.From(info.TargetFullRelease);
        public UpdateRelease Release { get; set; } = null!;
        public bool Downloaded, HandoffAttempted;
    }
    private sealed record AssetIdentity(string PackageId, string Version, string FileName, long Size, string SHA256, string SHA1)
    {
        public static AssetIdentity From(VelopackAsset asset) => new(asset.PackageId, asset.Version?.ToFullString() ?? "",
            asset.FileName, asset.Size, asset.SHA256, asset.SHA1);
    }

    /// <summary>Construction does not create a locator, client, updater process or file.</summary>
    public VelopackBackend() : this(() => new SdkSession()) { }
    internal VelopackBackend(Func<IVelopackUpdateSession> sessionFactory) =>
        this.sessionFactory = sessionFactory ?? throw new ArgumentNullException(nameof(sessionFactory));
    internal static IUpdateSource CreateSource(IFileDownloader downloader) => new VerifiedGithubSource(downloader);
    private IVelopackUpdateSession Session { get { lock (gate) return session ??= sessionFactory(); } }
    public bool IsManaged => Session.IsManaged;
    public string CurrentVersion => Session.CurrentVersion;

    public async Task<UpdateRelease?> CheckAsync(CancellationToken cancellationToken)
    {
        var active = Session;
        if (!active.IsManaged) return null;
        ValidateInstallation(active);
        long captured;
        lock (gate) { captured = ++generation; current = null; }
        var info = await active.CheckAsync(cancellationToken).WaitAsync(cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (info is null) return null;
        ValidateAsset(info.TargetFullRelease, fullOnly: true);
        if (info.IsDowngrade || !SemanticVersion.TryParse(active.CurrentVersion, out var installed) ||
            info.TargetFullRelease.Version <= installed)
            throw new InvalidDataException("UpdateNotNewer");
        var ticket = new Ticket(this, active, info);
        var release = new UpdateRelease(info.TargetFullRelease.Version.ToFullString(),
            info.TargetFullRelease.NotesMarkdown ?? "", info.TargetFullRelease.Size) { BackendToken = ticket };
        ticket.Release = release;
        lock (gate)
        {
            if (captured != generation) throw new OperationCanceledException("SupersededUpdateCheck");
            current = ticket;
        }
        return release;
    }

    public async Task DownloadAsync(UpdateRelease release, IProgress<int> progress, CancellationToken cancellationToken)
    {
        var ticket = GetTicket(release);
        ValidateInstallation(ticket.Session);
        ValidateAsset(ticket.Info.TargetFullRelease, fullOnly: true);
        ValidateTicketAsset(ticket);
        lock (gate)
        {
            if (!ReferenceEquals(current, ticket)) throw new InvalidDataException("ExpiredUpdateTicket");
            ticket.Downloaded = false;
        }
        await ticket.Session.DownloadAsync(ticket.Info, value => progress.Report(value), cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        lock (gate)
        {
            if (!ReferenceEquals(current, ticket)) throw new OperationCanceledException("SupersededUpdateDownload");
            ValidateTicketAsset(ticket);
            ticket.Downloaded = true;
        }
    }

    public void PrepareRestart(UpdateRelease release)
    {
        var ticket = GetTicket(release);
        ValidateInstallation(ticket.Session);
        ValidateAsset(ticket.Info.TargetFullRelease, fullOnly: true);
        ValidateTicketAsset(ticket);
        lock (gate)
        {
            if (!ReferenceEquals(current, ticket) || !ticket.Downloaded || ticket.HandoffAttempted)
                throw new InvalidOperationException("UpdateNotVerifiedOrAlreadyHandedOff");
            ticket.HandoffAttempted = true;
        }
        try { ticket.Session.PrepareRestart(ticket.Info); }
        catch { lock (gate) ticket.HandoffAttempted = false; throw; }
    }
    private Ticket GetTicket(UpdateRelease release)
    {
        ArgumentNullException.ThrowIfNull(release);
        lock (gate)
        {
            if (release.BackendToken is not Ticket ticket || !ReferenceEquals(ticket.Owner, this) ||
                !ReferenceEquals(current, ticket) || !ReferenceEquals(ticket.Release, release))
                throw new InvalidDataException("ForeignOrExpiredUpdateTicket");
            return ticket;
        }
    }
    private static void ValidateTicketAsset(Ticket ticket)
    {
        if (ticket.Identity != AssetIdentity.From(ticket.Info.TargetFullRelease)) throw new InvalidDataException("UpdateAssetChangedAfterCheck");
    }
    private static void ValidateInstallation(IVelopackUpdateSession session)
    {
        if (!session.IsManaged || session.AppId != PackageId || session.Channel != Channel)
            throw new InvalidDataException("UnexpectedInstallationIdentity");
    }

    internal static void ValidateAsset(VelopackAsset asset, bool fullOnly = false)
    {
        if (asset is null || asset.PackageId != PackageId || asset.Version is null || asset.Version.IsPrerelease ||
            !Regex.IsMatch(asset.Version.ToFullString(), @"^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)(?:\+[0-9A-Za-z.-]+)?$") ||
            asset.Size <= 0 || (asset.Type is not (VelopackAssetType.Full or VelopackAssetType.Delta)) ||
            (fullOnly && asset.Type != VelopackAssetType.Full))
            throw new InvalidDataException("InvalidUpdateAssetMetadata");
        string kind = asset.Type == VelopackAssetType.Full ? "full" : "delta";
        string expected = $"{PackageId}-{asset.Version.ToNormalizedString()}-{Channel}-{kind}.nupkg";
        if (!string.Equals(asset.FileName, expected, StringComparison.Ordinal) ||
            !Regex.IsMatch(asset.SHA256 ?? "", "^[0-9a-fA-F]{64}$"))
            throw new InvalidDataException("InvalidUpdateAssetNameOrHash");
    }

    private sealed class SdkSession : IVelopackUpdateSession
    {
        private readonly IVelopackLocator locator;
        private readonly UpdateManager manager;
        public SdkSession()
        {
            locator = VelopackLocator.Current;
            manager = new UpdateManager(new VerifiedGithubSource(new MetadataTimeoutDownloader()), new UpdateOptions
                { ExplicitChannel = VelopackBackend.Channel, AllowVersionDowngrade = false, MaximumDeltasBeforeFallback = -1 }, locator);
        }
        public bool IsManaged => manager.IsInstalled;
        public string AppId => locator.AppId ?? "";
        public string Channel => locator.Channel ?? "";
        public string CurrentVersion => manager.CurrentVersion?.ToFullString() ??
            typeof(VelopackBackend).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "1.0.0";
        public Task<UpdateInfo?> CheckAsync(CancellationToken cancellationToken) => manager.CheckForUpdatesAsync().WaitAsync(cancellationToken);
        public Task DownloadAsync(UpdateInfo info, Action<int> progress, CancellationToken cancellationToken) =>
            manager.DownloadUpdatesAsync(info, progress, cancellationToken);
        public void PrepareRestart(UpdateInfo info)
        {
            UpdateDirectoryWritePreflight.EnsureWritable([locator.RootAppDir, locator.AppContentDir, locator.PackagesDir]);
            manager.WaitExitThenApplyUpdates(info.TargetFullRelease, silent: false, restart: true);
        }
    }

    private sealed class VerifiedGithubSource(IFileDownloader downloader) : GithubSource(RepositoryUrl, null, false, downloader)
    {
        public override async Task<VelopackAssetFeed> GetReleaseFeed(IVelopackLogger logger, string? appId, string channel,
            Guid? stagingId = null, VelopackAsset? latestLocalRelease = null)
        {
            if (appId != PackageId || channel != VelopackBackend.Channel) throw new InvalidDataException("UnexpectedUpdateFeedIdentity");
            var feed = await base.GetReleaseFeed(logger, appId, channel, stagingId, latestLocalRelease).ConfigureAwait(false);
            if (feed.Assets.Length == 0) throw new UpdateFeedUnavailableException();
            foreach (var asset in feed.Assets) ValidateAsset(asset);
            return feed;
        }
    }
    private sealed class MetadataTimeoutDownloader : HttpClientFileDownloader
    {
        public override Task<byte[]> DownloadBytes(string url, IDictionary<string, string>? headers, double timeout) =>
            base.DownloadBytes(url, headers, Math.Min(timeout, 0.5));
        public override Task<string> DownloadString(string url, IDictionary<string, string>? headers, double timeout) =>
            base.DownloadString(url, headers, Math.Min(timeout, 0.5));
    }
}

internal sealed class UpdateFeedUnavailableException() : Exception("UpdateFeedNotPublished");

/// <summary>Injectable SDK boundary; fake sessions never create a locator, HTTP client or process.</summary>
internal interface IVelopackUpdateSession
{
    bool IsManaged { get; }
    string CurrentVersion { get; }
    string AppId { get; }
    string Channel { get; }
    Task<UpdateInfo?> CheckAsync(CancellationToken cancellationToken);
    Task DownloadAsync(UpdateInfo info, Action<int> progress, CancellationToken cancellationToken);
    void PrepareRestart(UpdateInfo info);
}
