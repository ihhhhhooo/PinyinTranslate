using System.Text.Json;
using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PinyinTranslate.Core;

// Only fixed, synthetic examples are accepted. The credential path is supplied by
// the caller and is never copied, printed, or persisted in the ledger.
internal static class Program
{
    [STAThread]
    private static async Task<int> Main(string[] args)
    {
        if (args.Length != 2 || args[1] is not ("candidate" or "reading" or "image" or "status"))
        { Console.Error.WriteLine("Usage: RealApiProbe <DPAPI file> <candidate|reading|image|status>"); return 2; }
        var ledgerPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PinyinTranslate", "api-budget.json");
        if (args[1] == "status")
        {
            try
            {
                var snapshot = new BudgetLedger(ledgerPath).Snapshot();
                Console.WriteLine($"Requests={snapshot.Entries.Count}; LimitRequests={snapshot.LimitRequests}; AccountedCny={snapshot.Entries.Sum(e => e.KnownCny ?? e.ReservedCny)}; LimitCny={snapshot.LimitCny}");
                return 0;
            }
            catch (Exception) { Console.Error.WriteLine("预算账本无法验证；已拒绝调用。"); return 1; }
        }
        using var http = new HttpClient();
        var client = new DeepSeekClient(http, new BudgetLedger(ledgerPath), () => SecretStore.Load(args[0]));
        try
        {
            if (args[1] == "candidate") Console.WriteLine(await client.TranslateAsync("属于", false, "fixed-candidate-belong", CancellationToken.None));
            else if (args[1] == "reading") Console.WriteLine(await client.TranslateAsync("If the temperature does not exceed 20 °C, store the samples for 48 hours.", true, "fixed-reading-condition", CancellationToken.None));
            else
            {
                byte[] png = SampleImage();
                Console.WriteLine(JsonSerializer.Serialize(await client.TranslateImageAsync(png, "fixed-image-heading-list", CancellationToken.None), new JsonSerializerOptions { WriteIndented = true }));
            }
            return 0;
        }
        catch (Exception e) { Console.Error.WriteLine(e.GetType().Name + ": 请求失败，详情未输出。"); return 1; }
    }

    private static byte[] SampleImage()
    {
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, 1000, 330));
            var text = new FormattedText("LAB NOTES\n1. Store samples below 20 °C.\n2. Do not open the container for 48 hours.",
                System.Globalization.CultureInfo.GetCultureInfo("en-US"), FlowDirection.LeftToRight,
                new Typeface("Arial"), 34, Brushes.Black, 1.0);
            dc.DrawText(text, new Point(25, 25));
        }
        var bitmap = new RenderTargetBitmap(1000, 330, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = new MemoryStream(); encoder.Save(stream); return stream.ToArray();
    }
}
