using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PinyinTranslate.Core;

/// <summary>Tracks a request lifecycle; development uses a budget and the desktop app uses unrestricted usage accounting.</summary>
public interface IApiRequestLedger
{
    Guid Reserve(string caseName, int maxInputTokens, int maxOutputTokens);
    void Complete(Guid id, int inputTokens, int outputTokens);
    // The development budget retains its original conservative ceiling and audit
    // format. Normal-use accounting overrides this overload to honor cache usage.
    void Complete(Guid id, ApiTokenUsage usage) => Complete(id, usage.InputTokens, usage.OutputTokens);
    void MarkUnknown(Guid id);
}

public sealed record BudgetEntry([property: JsonRequired] Guid Id, [property: JsonRequired] DateTimeOffset Time,
    [property: JsonRequired] string Model, [property: JsonRequired] string CaseName,
    [property: JsonRequired] int MaxInputTokens, [property: JsonRequired] int MaxOutputTokens,
    [property: JsonRequired] decimal ReservedCny, [property: JsonRequired] string Status,
    [property: JsonRequired] int? InputTokens = null, [property: JsonRequired] int? OutputTokens = null,
    [property: JsonRequired] decimal? KnownCny = null);
public sealed record BudgetSnapshot([property: JsonRequired] string TaskId, [property: JsonRequired] decimal LimitCny,
    [property: JsonRequired] int LimitRequests, [property: JsonRequired] List<BudgetEntry> Entries,
    [property: JsonRequired] string BalanceCheck = "未核对");

/// <summary>Persistent, cross-process, fail-closed task budget. Unknown outcomes retain their full reservation.</summary>
public sealed class BudgetLedger : IApiRequestLedger
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private readonly string _path;
    private readonly string _mutexName;
    private readonly decimal _limit;
    private readonly int _count;
    public BudgetLedger(string path, decimal limitCny = 1m, int limitRequests = 200)
    {
        if (limitCny <= 0 || limitRequests <= 0) throw new ArgumentOutOfRangeException(nameof(limitCny));
        _path = Path.GetFullPath(path); _limit = limitCny; _count = limitRequests;
        _mutexName = "Local\\PinyinTranslateBudget_" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(_path.ToUpperInvariant())));
    }
    private T Locked<T>(Func<T> action)
    {
        using var mutex = new Mutex(false, _mutexName);
        bool acquired = false;
        try
        {
            try { acquired = mutex.WaitOne(TimeSpan.FromSeconds(10)); }
            catch (AbandonedMutexException) { acquired = true; }
            if (!acquired) throw new TimeoutException("预算账本正被占用。");
            return action();
        }
        finally { if (acquired) mutex.ReleaseMutex(); }
    }
    private BudgetSnapshot Read()
    {
        if (!File.Exists(_path)) return new("PinyinTranslate-20261105", _limit, _count, []);
        try
        {
            var s = JsonSerializer.Deserialize<BudgetSnapshot>(File.ReadAllText(_path), Json);
            if (s is null || s.TaskId != "PinyinTranslate-20261105" || s.Entries is null || s.LimitCny <= 0 ||
                s.LimitRequests <= 0 || string.IsNullOrWhiteSpace(s.BalanceCheck) || s.Entries.Count > s.LimitRequests ||
                s.Entries.Any(InvalidEntry) || s.Entries.Select(e => e.Id).Distinct().Count() != s.Entries.Count)
                throw new InvalidDataException("无效的预算账本");
            return s;
        }
        catch (JsonException e) { throw new InvalidDataException("预算账本损坏，拒绝调用。", e); }
    }
    private static bool InvalidEntry(BudgetEntry? entry)
    {
        if (entry is null || entry.Id == Guid.Empty || entry.Time == default || entry.Model != "deepseek-flash" ||
            string.IsNullOrWhiteSpace(entry.CaseName) || entry.CaseName.Length > 120 ||
            entry.MaxInputTokens <= 0 || entry.MaxOutputTokens <= 0) return true;
        decimal expectedReservation = (entry.MaxInputTokens * 2m + entry.MaxOutputTokens * 8m) / 1_000_000m;
        if (entry.ReservedCny != expectedReservation) return true;
        if (entry.Status == "complete")
        {
            if (entry.InputTokens is not int input || entry.OutputTokens is not int output ||
                input < 0 || output < 0 || entry.KnownCny is not decimal known) return true;
            return known != (input * 2m + output * 8m) / 1_000_000m;
        }
        return (entry.Status != "in-flight" && entry.Status != "unknown") ||
            entry.InputTokens is not null || entry.OutputTokens is not null || entry.KnownCny is not null;
    }
    private void Write(BudgetSnapshot snapshot)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var temp = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var f = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            using (var writer = new StreamWriter(f, new UTF8Encoding(false))) { writer.Write(JsonSerializer.Serialize(snapshot, Json)); writer.Flush(); f.Flush(true); }
            File.Move(temp, _path, true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
    public BudgetSnapshot Snapshot() => Locked(Read);
    public BudgetSnapshot EffectiveSnapshot() => Locked(() =>
    {
        var snapshot = Read();
        return snapshot with { LimitCny = Math.Min(snapshot.LimitCny, _limit), LimitRequests = Math.Min(snapshot.LimitRequests, _count) };
    });
    /// <summary>Record only a balance outcome and optional CNY amount; never pass credentials or server response text.</summary>
    public void RecordBalanceCheck(bool succeeded, decimal? availableCny = null)
    {
        if (availableCny < 0 || (!succeeded && availableCny is not null)) throw new ArgumentException("余额核对结果无效。");
        Locked(() =>
        {
            var s = Read();
            string result = succeeded
                ? (availableCny is null ? "成功；余额未提供" : "成功；人民币可用余额=" + availableCny.Value.ToString(System.Globalization.CultureInfo.InvariantCulture))
                : "失败或不可用";
            Write(s with { BalanceCheck = DateTimeOffset.UtcNow.ToString("O", System.Globalization.CultureInfo.InvariantCulture) + " " + result });
            return 0;
        });
    }
    public Guid Reserve(string caseName, int maxInputTokens, int maxOutputTokens)
    {
        if (string.IsNullOrWhiteSpace(caseName) || caseName.Length > 120 || maxInputTokens <= 0 || maxOutputTokens <= 0) throw new ArgumentException("预算参数无效。");
        return Locked(() =>
        {
            var s = Read();
            // Peak cache-miss input and peak output; no assumptions about discounts.
            decimal estimate = (maxInputTokens * 2m + maxOutputTokens * 8m) / 1_000_000m;
            decimal used = s.Entries.Sum(e => e.KnownCny ?? e.ReservedCny);
            if (s.Entries.Count >= Math.Min(s.LimitRequests, _count) || used + estimate > Math.Min(s.LimitCny, _limit))
                throw new InvalidOperationException("已达到请求次数或预算上限。");
            var entry = new BudgetEntry(Guid.NewGuid(), DateTimeOffset.UtcNow, "deepseek-flash", caseName, maxInputTokens, maxOutputTokens, estimate, "in-flight");
            s.Entries.Add(entry); Write(s); return entry.Id;
        });
    }
    public void Complete(Guid id, int inputTokens, int outputTokens)
    {
        if (inputTokens < 0 || outputTokens < 0) throw new ArgumentOutOfRangeException(nameof(inputTokens));
        Locked(() =>
        {
            var s = Read(); int i = s.Entries.FindIndex(e => e.Id == id);
            if (i < 0 || s.Entries[i].Status != "in-flight") throw new InvalidOperationException("预算预留不存在或已完成。");
            var e = s.Entries[i]; decimal cost = (inputTokens * 2m + outputTokens * 8m) / 1_000_000m;
            // If usage exceeds estimates, preserve the conservative higher actual amount and prevent new calls.
            s.Entries[i] = e with { Status = "complete", InputTokens = inputTokens, OutputTokens = outputTokens, KnownCny = cost };
            Write(s); return 0;
        });
    }
    public void MarkUnknown(Guid id) => Locked(() =>
    {
        var s = Read(); int i = s.Entries.FindIndex(e => e.Id == id);
        if (i >= 0 && s.Entries[i].Status == "in-flight") { s.Entries[i] = s.Entries[i] with { Status = "unknown" }; Write(s); }
        return 0;
    });
}
