using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PinyinTranslate.Core;

public sealed record ApiUsageDay(
    [property: JsonRequired] long Requests,
    [property: JsonRequired] long WithUsage,
    [property: JsonRequired] long Unknown,
    [property: JsonRequired] long InputTokens,
    [property: JsonRequired] long OutputTokens,
    [property: JsonRequired] decimal EstimatedCny)
{
    [JsonRequired] public long CacheHitTokens { get; init; }
    [JsonRequired] public long CacheMissTokens { get; init; }
    [JsonRequired] public long UnclassifiedInputTokens { get; init; }
    [JsonRequired] public long MissingCacheDetails { get; init; }
    [JsonRequired] public long MissingCalendar { get; init; }
    [JsonRequired] public long PeakWithUsage { get; init; }
    [JsonRequired] public long OffPeakWithUsage { get; init; }
    [JsonRequired] public long LegacyWithUsage { get; init; }
    [JsonRequired] public long LegacyInputTokens { get; init; }
    [JsonRequired] public long LegacyOutputTokens { get; init; }
    [JsonRequired] public decimal LegacyEstimatedCny { get; init; }
}

public sealed record ApiUsagePending([property: JsonRequired] string Day, [property: JsonRequired] DateTimeOffset StartedUtc);

public sealed record ApiUsageState(
    [property: JsonRequired] int Version,
    [property: JsonRequired] Dictionary<string, ApiUsageDay> Days,
    [property: JsonRequired] Dictionary<Guid, ApiUsagePending> Pending);

public sealed record ApiUsageDaySnapshot(string Day, long Requests, long MissingUsage,
    long InputTokens, long OutputTokens, decimal EstimatedCny)
{
    public long CacheHitTokens { get; init; }
    public long CacheMissTokens { get; init; }
    public long UnclassifiedInputTokens { get; init; }
    public long MissingCacheDetails { get; init; }
    public long MissingCalendar { get; init; }
    public long LegacyWithUsage { get; init; }
    public decimal LegacyEstimatedCny { get; init; }
}

public sealed record ApiUsageSnapshot(IReadOnlyList<ApiUsageDaySnapshot> Days, string? Warning);

/// <summary>
/// Records aggregate normal-use API statistics without a request or money limit.
/// It never saves the key, request content, screenshots or translations. Recording
/// errors do not prevent a translation and are reported in the statistics view.
/// </summary>
public sealed class ApiUsageLedger : IApiRequestLedger
{
    private readonly object _gate = new();
    private readonly string _path;
    private readonly TimeProvider _clock;
    private ApiUsageState _state = new(2, new(), new());
    private bool _mayWrite = true;
    private bool _needsLegacyBackup;
    private string? _warning;
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public ApiUsageLedger(string path, TimeProvider? clock = null)
    {
        _path = path;
        _clock = clock ?? TimeProvider.System;
        try
        {
            if (!File.Exists(_path)) return;
            string text = File.ReadAllText(_path);
            using var document = JsonDocument.Parse(text);
            if (document.RootElement.ValueKind != JsonValueKind.Object || !document.RootElement.TryGetProperty("Version", out var version) ||
                version.ValueKind != JsonValueKind.Number || !version.TryGetInt32(out int number)) throw new InvalidDataException("无效的 API 用量记录。");
            var loaded = number == 1 ? Migrate(text) : number == 2 ? JsonSerializer.Deserialize<ApiUsageState>(text, Json) : null;
            if (loaded is null || !Valid(loaded)) throw new InvalidDataException("无效的 API 用量记录。");
            _needsLegacyBackup = number == 1;
            // A restarted process cannot receive completions for prior in-flight IDs.
            // Keep their counts as unknown instead of silently dropping them.
            foreach (var pending in loaded.Pending.Values)
                loaded.Days[pending.Day] = loaded.Days[pending.Day] with { Unknown = loaded.Days[pending.Day].Unknown + 1 };
            loaded.Pending.Clear();
            _state = loaded;
        }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or NotSupportedException or ArgumentException)
        {
            _mayWrite = false;
            _warning = "新版 API 用量记录无法读取，本次新增用量只保存在内存中。";
        }
    }

    public Guid Reserve(string caseName, int maxInputTokens, int maxOutputTokens)
    {
        var id = Guid.NewGuid();
        var started = _clock.GetUtcNow();
        string day = FlashPricing.BeijingDate(started).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        lock (_gate)
        {
            if (!_state.Days.TryGetValue(day, out var usage)) usage = new(0, 0, 0, 0, 0, 0m);
            _state.Days[day] = usage with { Requests = usage.Requests + 1 };
            _state.Pending[id] = new(day, started);
            Persist();
        }
        return id;
    }

    public void Complete(Guid id, int inputTokens, int outputTokens) => Complete(id, new ApiTokenUsage(inputTokens, outputTokens));

    public void Complete(Guid id, ApiTokenUsage tokens)
    {
        ArgumentNullException.ThrowIfNull(tokens);
        if (tokens.InputTokens < 0 || tokens.OutputTokens < 0) throw new ArgumentOutOfRangeException(nameof(tokens));
        lock (_gate)
        {
            if (!_state.Pending.TryGetValue(id, out var pending) || !_state.Days.TryGetValue(pending.Day, out var usage)) return;
            var quote = FlashPricing.At(pending.StartedUtc);
            var (hit, miss, known) = tokens.CacheBreakdown();
            var updated = usage with
            {
                WithUsage = checked(usage.WithUsage + 1),
                InputTokens = checked(usage.InputTokens + tokens.InputTokens),
                OutputTokens = checked(usage.OutputTokens + tokens.OutputTokens),
                EstimatedCny = usage.EstimatedCny + FlashPricing.Estimate(tokens, quote),
                CacheHitTokens = checked(usage.CacheHitTokens + (known ? hit : 0)),
                CacheMissTokens = checked(usage.CacheMissTokens + (known ? miss : 0)),
                UnclassifiedInputTokens = checked(usage.UnclassifiedInputTokens + (known ? 0 : tokens.InputTokens)),
                MissingCacheDetails = usage.MissingCacheDetails + (known ? 0 : 1),
                MissingCalendar = usage.MissingCalendar + (quote.CalendarKnown ? 0 : 1),
                PeakWithUsage = usage.PeakWithUsage + (quote.IsPeak ? 1 : 0),
                OffPeakWithUsage = usage.OffPeakWithUsage + (quote.IsPeak ? 0 : 1)
            };
            _state.Pending.Remove(id);
            _state.Days[pending.Day] = updated;
            Persist();
        }
    }

    public void MarkUnknown(Guid id)
    {
        lock (_gate)
        {
            if (!_state.Pending.Remove(id, out var pending) || !_state.Days.TryGetValue(pending.Day, out var usage)) return;
            _state.Days[pending.Day] = usage with { Unknown = usage.Unknown + 1 };
            Persist();
        }
    }

    public ApiUsageSnapshot Snapshot()
    {
        lock (_gate)
        {
            var days = _state.Days.Select(pair => new ApiUsageDaySnapshot(pair.Key, pair.Value.Requests,
                pair.Value.Unknown + _state.Pending.LongCount(p => p.Value.Day == pair.Key), pair.Value.InputTokens,
                pair.Value.OutputTokens, pair.Value.EstimatedCny)
            {
                CacheHitTokens = pair.Value.CacheHitTokens, CacheMissTokens = pair.Value.CacheMissTokens,
                UnclassifiedInputTokens = pair.Value.UnclassifiedInputTokens, MissingCacheDetails = pair.Value.MissingCacheDetails,
                MissingCalendar = pair.Value.MissingCalendar, LegacyWithUsage = pair.Value.LegacyWithUsage,
                LegacyEstimatedCny = pair.Value.LegacyEstimatedCny
            }).ToArray();
            return new(days, _warning);
        }
    }

    private static bool DayKey(string day) => DateOnly.TryParseExact(day, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _);
    private static bool Valid(ApiUsageState state) => state.Version == 2 && state.Days is not null && state.Pending is not null &&
        state.Pending.All(p => p.Key != Guid.Empty && p.Value is not null && !string.IsNullOrWhiteSpace(p.Value.Day) && state.Days.ContainsKey(p.Value.Day) &&
            p.Value.StartedUtc != default && FlashPricing.BeijingDate(p.Value.StartedUtc).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) == p.Value.Day) &&
        state.Days.All(pair => DayKey(pair.Key) && pair.Value is { } d &&
            new[] { d.Requests, d.WithUsage, d.Unknown, d.InputTokens, d.OutputTokens, d.CacheHitTokens, d.CacheMissTokens,
                d.UnclassifiedInputTokens, d.MissingCacheDetails, d.MissingCalendar, d.PeakWithUsage, d.OffPeakWithUsage,
                d.LegacyWithUsage, d.LegacyInputTokens, d.LegacyOutputTokens }.All(n => n >= 0) &&
            d.EstimatedCny >= 0 && d.LegacyEstimatedCny >= 0 &&
            (decimal)d.Requests == (decimal)d.WithUsage + d.Unknown + state.Pending.LongCount(p => p.Value.Day == pair.Key) &&
            (decimal)d.InputTokens == (decimal)d.CacheHitTokens + d.CacheMissTokens + d.UnclassifiedInputTokens + d.LegacyInputTokens &&
            d.LegacyOutputTokens <= d.OutputTokens && (decimal)d.WithUsage == (decimal)d.PeakWithUsage + d.OffPeakWithUsage + d.LegacyWithUsage &&
            d.MissingCacheDetails <= (decimal)d.PeakWithUsage + d.OffPeakWithUsage && d.MissingCalendar <= (decimal)d.PeakWithUsage + d.OffPeakWithUsage);

    private sealed record LegacyDay([property: JsonRequired] long Requests, [property: JsonRequired] long WithUsage,
        [property: JsonRequired] long Unknown, [property: JsonRequired] long InputTokens, [property: JsonRequired] long OutputTokens,
        [property: JsonRequired] decimal EstimatedCny);
    private sealed record LegacyState([property: JsonRequired] int Version,
        [property: JsonRequired] Dictionary<string, LegacyDay> Days, [property: JsonRequired] Dictionary<Guid, string> Pending);

    private static ApiUsageState Migrate(string text)
    {
        var old = JsonSerializer.Deserialize<LegacyState>(text, Json);
        if (old?.Days is null || old.Pending is null || old.Pending.Any(p => p.Key == Guid.Empty || string.IsNullOrWhiteSpace(p.Value) || !old.Days.ContainsKey(p.Value)) ||
            old.Days.Any(pair => !DayKey(pair.Key) || pair.Value is null ||
                new[] { pair.Value.Requests, pair.Value.WithUsage, pair.Value.Unknown, pair.Value.InputTokens, pair.Value.OutputTokens }.Any(n => n < 0) ||
                pair.Value.EstimatedCny < 0 || (decimal)pair.Value.Requests != (decimal)pair.Value.WithUsage + pair.Value.Unknown + old.Pending.LongCount(p => p.Value == pair.Key)))
            throw new InvalidDataException("无效的旧版 API 用量记录。");
        var days = old.Days.ToDictionary(p => p.Key, p => new ApiUsageDay(p.Value.Requests, p.Value.WithUsage,
            p.Value.Unknown + old.Pending.LongCount(pending => pending.Value == p.Key), p.Value.InputTokens, p.Value.OutputTokens, 0m)
        {
            LegacyWithUsage = p.Value.WithUsage, LegacyInputTokens = p.Value.InputTokens,
            LegacyOutputTokens = p.Value.OutputTokens, LegacyEstimatedCny = p.Value.EstimatedCny
        });
        return new(2, days, new());
    }

    private void Persist()
    {
        if (!_mayWrite) return;
        string temp = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(temp, JsonSerializer.Serialize(_state, Json));
            if (_needsLegacyBackup)
            {
                string backup = _path + ".v1-backup.json";
                if (!File.Exists(backup)) File.Copy(_path, backup, overwrite: false);
                _needsLegacyBackup = false;
            }
            File.Move(temp, _path, true);
            _warning = null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            _warning = "新版 API 用量暂时无法保存，本次新增用量保留在内存中；后续记录时会重试。";
        }
        finally
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch (Exception) { }
        }
    }
}
