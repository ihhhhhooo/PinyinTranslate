using System.Text;
using System.Net;
using System.Text.Json;
using Velopack;
using Velopack.Logging;
using Velopack.Sources;

namespace PinyinTranslate.Update;

/// <summary>One stable feed request; downloads stay pinned to the version checked by the user.</summary>
internal sealed class LatestGithubUpdateSource(IFileDownloader downloader) : IUpdateSource
{
    internal const string FeedUrl = VelopackBackend.RepositoryUrl + "/releases/latest/download/releases.win-x64.json";

    public async Task<VelopackAssetFeed> GetReleaseFeed(IVelopackLogger logger, string? appId, string channel,
        Guid? stagingId = null, VelopackAsset? latestLocalRelease = null)
    {
        if (appId != VelopackBackend.PackageId || channel != VelopackBackend.Channel)
            throw new InvalidDataException("UnexpectedUpdateFeedIdentity");
        byte[] bytes;
        try { bytes = await downloader.DownloadBytes(FeedUrl, null, 0.25).ConfigureAwait(false); }
        catch (HttpRequestException error) when (error.StatusCode == HttpStatusCode.NotFound)
        { throw new UpdateFeedUnavailableException(); }
        VelopackAssetFeed feed;
        try { feed = VelopackAssetFeed.FromJson(Encoding.UTF8.GetString(bytes).TrimStart('\uFEFF')); }
        catch (JsonException error) { throw new InvalidDataException("InvalidUpdateFeed", error); }
        if (feed.Assets is null || feed.Assets.Length == 0) throw new UpdateFeedUnavailableException();
        foreach (var asset in feed.Assets) VelopackBackend.ValidateAsset(asset);
        if (!feed.Assets.Any(asset => asset.Type == VelopackAssetType.Full)) throw new UpdateFeedUnavailableException();
        return feed;
    }

    public Task DownloadReleaseEntry(IVelopackLogger logger, VelopackAsset releaseEntry, string localFile,
        Action<int> progress, CancellationToken cancelToken)
    {
        VelopackBackend.ValidateAsset(releaseEntry);
        // Never use /latest for a package: a newer release can appear after the check.
        string tag = Uri.EscapeDataString("v" + releaseEntry.Version.ToNormalizedString());
        string url = VelopackBackend.RepositoryUrl + "/releases/download/" + tag + "/" + Uri.EscapeDataString(releaseEntry.FileName);
        return downloader.DownloadFile(url, localFile, progress, null, 30, cancelToken);
    }
}
