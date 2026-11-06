using System.Text.Json.Nodes;
using PinyinTranslate.Core;

namespace PinyinTranslate.Core.Tests;

public sealed class BudgetLedgerIntegrityTests
{
    private static string NewLedger()
    {
        var path = Path.Combine(Path.GetTempPath(), "PinyinTranslate-tests", Guid.NewGuid().ToString("N"), "budget.json");
        var ledger = new BudgetLedger(path);
        Guid id = ledger.Reserve("integrity-test", 1000, 500);
        ledger.Complete(id, 50, 40);
        return path;
    }
    private static void Mutate(string path, Action<JsonObject, JsonObject> change)
    {
        var document = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        var entry = document["Entries"]!.AsArray()[0]!.AsObject();
        change(document, entry);
        File.WriteAllText(path, document.ToJsonString());
    }
    [Theory]
    [InlineData("missing-input")]
    [InlineData("missing-reserve")]
    [InlineData("negative-input")]
    [InlineData("negative-output")]
    [InlineData("wrong-usage-cost")]
    [InlineData("wrong-reserve")]
    [InlineData("invalid-status")]
    [InlineData("unknown-with-usage")]
    [InlineData("missing-entries")]
    [InlineData("missing-balance")]
    public void CorruptOrIncompleteLedgerRefusesFurtherReservations(string defect)
    {
        string path = NewLedger();
        Mutate(path, (document, entry) =>
        {
            switch (defect)
            {
                case "missing-input": entry.Remove("InputTokens"); break;
                case "missing-reserve": entry.Remove("ReservedCny"); break;
                case "negative-input": entry["InputTokens"] = -1; break;
                case "negative-output": entry["OutputTokens"] = -1; break;
                case "wrong-usage-cost": entry["KnownCny"] = 0; break;
                case "wrong-reserve": entry["ReservedCny"] = 0.000001m; break;
                case "invalid-status": entry["Status"] = "success"; break;
                case "unknown-with-usage": entry["Status"] = "unknown"; entry["KnownCny"] = null; break;
                case "missing-entries": document.Remove("Entries"); break;
                case "missing-balance": document.Remove("BalanceCheck"); break;
                default: throw new InvalidOperationException();
            }
        });
        Assert.Throws<InvalidDataException>(() => new BudgetLedger(path).Reserve("next", 100, 100));
    }
    [Fact]
    public void ValidCompleteAndUnknownEntriesRemainReadable()
    {
        string path = NewLedger(); var ledger = new BudgetLedger(path);
        Guid id = ledger.Reserve("unknown", 100, 100); ledger.MarkUnknown(id);
        var snapshot = new BudgetLedger(path).Snapshot();
        Assert.Equal(2, snapshot.Entries.Count);
        Assert.Equal("complete", snapshot.Entries[0].Status);
        Assert.Equal("unknown", snapshot.Entries[1].Status);
    }
}
