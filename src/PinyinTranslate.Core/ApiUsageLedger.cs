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
    [property: JsonRequired] decimal EstimatedCny);

public sealed record ApiUsageState(
    [property: JsonRequired] int Version,
    [property: JsonRequired] Dictionary<string, ApiUsageDay> Days,
    [property: JsonRequired] Dictionary<Guid, string> Pending);

public sealed record ApiUsageDaySnapshot(string Day, long Requests, long MissingUsage,
    long InputTokens, long OutputTokens, decimal EstimatedCny);

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
    private ApiUsageState _state = new(1, new(), new());
    private bool _mayWrite = true;
    private string? _warning;
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public ApiUsageLedger(string path)
    {
        _path = path;
        try
        {
            if (!File.Exists(_path)) return;
            var loaded = JsonSerializer.Deserialize<ApiUsageState>(File.ReadAllText(_path), Json);
            if (loaded is null || loaded.Version != 1 || loaded.Days is null || loaded.Pending is null ||
                loaded.Days.Any(pair => !DateOnly.TryParseExact(pair.Key, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _) ||
                    pair.Value is null || pair.Value.Requests < 0 || pair.Value.WithUsage < 0 || pair.Value.Unknown < 0 ||
                    pair.Value.InputTokens < 0 || pair.Value.OutputTokens < 0 || pair.Value.EstimatedCny < 0) ||
                loaded.Pending.Any(pair => pair.Key == Guid.Empty || string.IsNullOrWhiteSpace(pair.Value) || !loaded.Days.ContainsKey(pair.Value)) ||
                loaded.Days.Any(pair => pair.Value.Requests != pair.Value.WithUsage + pair.Value.Unknown +
                    loaded.Pending.LongCount(p => p.Value == pair.Key)))
                throw new InvalidDataException("无效的 API 用量记录。");
            _state = loaded;
        }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            _mayWrite = false;
            _warning = "新版 API 用量记录无法读取，本次新增用量只保存在内存中。";
        }
    }

    public Guid Reserve(string caseName, int maxInputTokens, int maxOutputTokens)
    {
        var id = Guid.NewGuid();
        string day = DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        lock (_gate)
        {
            if (!_state.Days.TryGetValue(day, out var usage)) usage = new(0, 0, 0, 0, 0, 0m);
            _state.Days[day] = usage with { Requests = usage.Requests + 1 };
            _state.Pending[id] = day;
            Persist();
        }
        return id;
    }

    public void Complete(Guid id, int inputTokens, int outputTokens)
    {
        lock (_gate)
        {
            if (!_state.Pending.Remove(id, out var day) || !_state.Days.TryGetValue(day, out var usage)) return;
            _state.Days[day] = usage with
            {
                WithUsage = usage.WithUsage + 1,
                InputTokens = usage.InputTokens + inputTokens,
                OutputTokens = usage.OutputTokens + outputTokens,
                EstimatedCny = usage.EstimatedCny + (inputTokens * 2m + outputTokens * 8m) / 1_000_000m
            };
            Persist();
        }
    }

    public void MarkUnknown(Guid id)
    {
        lock (_gate)
        {
            if (!_state.Pending.Remove(id, out var day) || !_state.Days.TryGetValue(day, out var usage)) return;
            _state.Days[day] = usage with { Unknown = usage.Unknown + 1 };
            Persist();
        }
    }

    public ApiUsageSnapshot Snapshot()
    {
        lock (_gate)
        {
            var days = _state.Days.Select(pair => new ApiUsageDaySnapshot(pair.Key, pair.Value.Requests,
                pair.Value.Unknown + _state.Pending.LongCount(p => p.Value == pair.Key), pair.Value.InputTokens,
                pair.Value.OutputTokens, pair.Value.EstimatedCny)).ToArray();
            return new(days, _warning);
        }
    }

    private void Persist()
    {
        if (!_mayWrite) return;
        string temp = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(temp, JsonSerializer.Serialize(_state, Json));
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
