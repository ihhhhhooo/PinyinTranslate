using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace PinyinTranslate.App;

public partial class StatisticsPage : UserControl, IDisposable
{
    private readonly IAppServices _services;
    private bool _disposed;
    private bool _refreshing;
    private bool _refreshPending;
    private string? _summary;

    public StatisticsPage(IAppServices services)
    {
        _services = services;
        InitializeComponent();
        _services.StatisticsChanged += StatisticsChanged;
        IsVisibleChanged += (_, _) => { if (IsVisible) RequestRefresh(); };
    }

    private void StatisticsChanged(object? sender, EventArgs e)
    {
        if (_disposed || Dispatcher.HasShutdownStarted) return;
        try { Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => { if (IsVisible) RequestRefresh(); })); }
        catch (InvalidOperationException) { }
    }

    private async void RequestRefresh()
    {
        if (_disposed || !IsVisible) return;
        if (_refreshing) { _refreshPending = true; return; }
        _refreshing = true;
        try
        {
            do
            {
                _refreshPending = false;
                var snapshot = await Task.Run(_services.GetStatistics);
                if (!_disposed && IsVisible) ShowSnapshot(snapshot);
            } while (_refreshPending && !_disposed && IsVisible);
        }
        catch (Exception)
        {
            if (!_disposed)
            {
                StatisticsStatus.Text = "统计读取失败，请稍后刷新。";
                StatisticsStatus.SetResourceReference(TextBlock.ForegroundProperty, "ErrorInk");
            }
        }
        finally { _refreshing = false; }
    }

    private void ShowSnapshot(StatisticsSnapshot snapshot)
    {
        var t = snapshot.Translation;
        long results = t.SelectionResults + t.ScreenshotResults;
        ResultsValue.Text = Number(results);
        SourceValue.Text = Number(t.SourceCharacters);
        TranslationDetails.Text = string.Join("\n",
            Number(t.SelectionResults) + " / " + Number(t.ScreenshotResults),
            Number(t.TranslationCharacters),
            Number(t.OriginalCopies) + " / " + Number(t.TranslationCopies));
        StatisticsSince.Text = "字数、复制与翻译次数从新版开始记录（" + t.SinceUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.CurrentCulture) +
            " 起），旧版数据无法补录。字符包含空格与标点；仅保存汇总数字。";

        _summary = BuildTranslationSummary(t, results);
        if (snapshot.Api is not { } api)
        {
            TokenValue.Text = "—";
            CostValue.Text = "—";
            ApiDetails.Text = string.Join("\n", Enumerable.Repeat("—", 5));
            _summary += "\nAPI 用量暂不可读取。";
        }
        else
        {
            TokenValue.Text = Number(api.InputTokens + api.OutputTokens);
            CostValue.Text = Money(api.EstimatedCny);
            ApiDetails.Text = string.Join("\n",
                Number(api.Requests),
                Number(api.InputTokens) + " / " + Number(api.OutputTokens),
                Number(api.MissingUsage),
                Number(api.TodayRequests) + " / " + Number(api.TodayTokens),
                Money(api.TodayEstimatedCny));
            _summary += "\nAPI 用量\n请求次数：" + Number(api.Requests) +
                "\n输入 / 输出 Token：" + Number(api.InputTokens) + " / " + Number(api.OutputTokens) +
                "\n未返回用量的请求：" + Number(api.MissingUsage) +
                "\n已知用量估算费用：" + Money(api.EstimatedCny) +
                "\n今日请求 / Token：" + Number(api.TodayRequests) + " / " + Number(api.TodayTokens) +
                "\n今日估算费用：" + Money(api.TodayEstimatedCny) +
                "\n费用为本地估算，不含未返回用量的请求；旧版记录可能包含开发测试请求。";
        }
        if (!string.IsNullOrWhiteSpace(snapshot.Warning)) _summary += "\n提示：" + snapshot.Warning;
        CopySummaryButton.IsEnabled = true;
        StatisticsStatus.Text = string.IsNullOrWhiteSpace(snapshot.Warning)
            ? "已更新 " + DateTime.Now.ToString("HH:mm:ss", CultureInfo.CurrentCulture)
            : snapshot.Warning;
        StatisticsStatus.SetResourceReference(TextBlock.ForegroundProperty, string.IsNullOrWhiteSpace(snapshot.Warning) ? "Muted" : "ErrorInk");
    }

    private static string BuildTranslationSummary(TranslationStatistics t, long results) =>
        "PinyinTranslate 使用统计\n统计起点：" + t.SinceUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.CurrentCulture) +
        "\n成功取得译文：" + Number(results) + "（划词 " + Number(t.SelectionResults) + "，截图 " + Number(t.ScreenshotResults) + "）" +
        "\n原文 / 译文字符：" + Number(t.SourceCharacters) + " / " + Number(t.TranslationCharacters) +
        "\n原文 / 译文复制：" + Number(t.OriginalCopies) + " / " + Number(t.TranslationCopies) +
        "\n字数、复制与翻译次数从新版起记录，旧版无法补录；字符包含空格和标点。";

    private static string Number(long n) => n.ToString("N0", CultureInfo.CurrentCulture);
    private static string Money(decimal n) => "¥" + n.ToString("0.000000", CultureInfo.InvariantCulture);
    private void RefreshClicked(object sender, RoutedEventArgs e) => RequestRefresh();

    private async void CopySummaryClicked(object sender, RoutedEventArgs e)
    {
        if (_disposed) return;
        try
        {
            CopySummaryButton.IsEnabled = false;
            var snapshot = await Task.Run(_services.GetStatistics);
            if (_disposed) return;
            ShowSnapshot(snapshot);
            if (_summary != null) Clipboard.SetText(_summary);
            StatisticsStatus.Text = "统计摘要已复制。" + (snapshot.Warning ?? "");
            StatisticsStatus.SetResourceReference(TextBlock.ForegroundProperty, string.IsNullOrWhiteSpace(snapshot.Warning) ? "Muted" : "ErrorInk");
        }
        catch (Exception)
        {
            if (!_disposed)
            {
                StatisticsStatus.Text = "复制失败，请检查剪贴板后重试。";
                StatisticsStatus.SetResourceReference(TextBlock.ForegroundProperty, "ErrorInk");
            }
        }
        finally { if (!_disposed) CopySummaryButton.IsEnabled = _summary != null; }
    }

    public void Dispose()
    {
        _disposed = true;
        _services.StatisticsChanged -= StatisticsChanged;
    }
}
