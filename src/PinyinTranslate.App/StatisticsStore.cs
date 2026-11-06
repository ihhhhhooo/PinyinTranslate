using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using PinyinTranslate.Core;

namespace PinyinTranslate.App;

/// <summary>Only aggregate counts are stored; original text, translations, screenshots and keys are never stored here.</summary>
public sealed record TranslationStatistics
{
    [JsonRequired] public int Version { get; init; } = 1;
    [JsonRequired] public DateTimeOffset SinceUtc { get; init; }
    [JsonRequired] public long SelectionResults { get; init; }
    [JsonRequired] public long ScreenshotResults { get; init; }
    [JsonRequired] public long SelectionFailures { get; init; }
    [JsonRequired] public long ScreenshotFailures { get; init; }
    [JsonRequired] public long SelectionCanceled { get; init; }
    [JsonRequired] public long ScreenshotCanceled { get; init; }
    [JsonRequired] public long SourceCharacters { get; init; }
    [JsonRequired] public long TranslationCharacters { get; init; }
    [JsonRequired] public long CachedResults { get; init; }
    [JsonRequired] public long SharedResults { get; init; }
    [JsonRequired] public long OriginalCopies { get; init; }
    [JsonRequired] public long TranslationCopies { get; init; }
    [JsonRequired] public long CopiedCharacters { get; init; }
}

public sealed record ApiStatistics(long Requests, long InputTokens, long OutputTokens, decimal EstimatedCny,
    long MissingUsage, long TodayRequests, long TodayTokens, decimal TodayEstimatedCny)
{
    public long CacheHitTokens { get; init; }
    public long CacheMissTokens { get; init; }
    public long UnclassifiedInputTokens { get; init; }
    public long MissingCacheDetails { get; init; }
    public long MissingCalendar { get; init; }
    public long LegacyPricingRequests { get; init; }
    public decimal LegacyEstimatedCny { get; init; }
    public FlashPriceQuote? CurrentPricing { get; init; }
}
public sealed record StatisticsSnapshot(TranslationStatistics Translation, ApiStatistics? Api, string? Warning);

internal sealed class StatisticsStore
{
    private readonly object _gate = new();
    private readonly string _path;
    private TranslationStatistics _data = new() { SinceUtc = DateTimeOffset.UtcNow };
    private bool _mayWrite = true;
    private string? _warning;
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    public event EventHandler? Changed;

    public StatisticsStore(string path)
    {
        _path = path;
        try
        {
            if (!File.Exists(_path)) return;
            var loaded = JsonSerializer.Deserialize<TranslationStatistics>(File.ReadAllText(_path), Json);
            if (loaded is null || loaded.Version != 1 || loaded.SinceUtc == default ||
                new[] { loaded.SelectionResults, loaded.ScreenshotResults, loaded.SelectionFailures, loaded.ScreenshotFailures,
                    loaded.SelectionCanceled, loaded.ScreenshotCanceled, loaded.SourceCharacters, loaded.TranslationCharacters,
                    loaded.CachedResults, loaded.SharedResults, loaded.OriginalCopies, loaded.TranslationCopies, loaded.CopiedCharacters }.Any(x => x < 0))
                throw new InvalidDataException("无效的统计数据");
            _data = loaded;
        }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            // Never replace unreadable historic data with an apparently valid empty file.
            _mayWrite = false;
            _warning = "历史统计文件无法读取，本次新增数据仅保存在内存中；请检查本地统计文件。";
        }
    }

    public (TranslationStatistics Data, string? Warning) Snapshot()
    {
        lock (_gate) return (_data, _warning);
    }

    public static long Characters(string text) => new StringInfo(text).LengthInTextElements;

    public void Result(bool screenshot, long sourceChars, long translatedChars, CacheDisposition origin = CacheDisposition.NewRequest) =>
        Update(s => s with
        {
            SelectionResults = s.SelectionResults + (screenshot ? 0 : 1),
            ScreenshotResults = s.ScreenshotResults + (screenshot ? 1 : 0),
            SourceCharacters = s.SourceCharacters + sourceChars,
            TranslationCharacters = s.TranslationCharacters + translatedChars,
            CachedResults = s.CachedResults + (origin == CacheDisposition.Cached ? 1 : 0),
            SharedResults = s.SharedResults + (origin == CacheDisposition.SharedRequest ? 1 : 0)
        });

    public void Failure(bool screenshot, bool canceled) =>
        Update(s => s with
        {
            SelectionFailures = s.SelectionFailures + (!screenshot && !canceled ? 1 : 0),
            ScreenshotFailures = s.ScreenshotFailures + (screenshot && !canceled ? 1 : 0),
            SelectionCanceled = s.SelectionCanceled + (!screenshot && canceled ? 1 : 0),
            ScreenshotCanceled = s.ScreenshotCanceled + (screenshot && canceled ? 1 : 0)
        });

    public void Copy(bool original, long characters) =>
        Update(s => s with
        {
            OriginalCopies = s.OriginalCopies + (original ? 1 : 0),
            TranslationCopies = s.TranslationCopies + (original ? 0 : 1),
            CopiedCharacters = s.CopiedCharacters + characters
        });

    private void Update(Func<TranslationStatistics, TranslationStatistics> change)
    {
        lock (_gate)
        {
            _data = change(_data);
            if (_mayWrite)
            {
                string temp = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                    File.WriteAllText(temp, JsonSerializer.Serialize(_data, Json));
                    File.Move(temp, _path, true);
                    _warning = null;
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
                {
                    _warning = "统计暂时无法保存，本次新增数据保留在内存中；后续记录时会重试。";
                }
                finally
                {
                    try { if (File.Exists(temp)) File.Delete(temp); } catch (Exception) { }
                }
            }
        }
        try { Changed?.Invoke(this, EventArgs.Empty); } catch (Exception) { }
    }
}
