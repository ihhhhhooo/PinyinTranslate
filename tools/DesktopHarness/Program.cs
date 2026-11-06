using System.IO;
using System.Windows;
using System.Text.Json;
using PinyinTranslate.App;
using PinyinTranslate.Core;

internal static class Program
{
    private static readonly object DiagnosticLock = new();
    [STAThread]
    private static void Main()
    {
        var testSettings = Path.Combine(Path.GetTempPath(), "PinyinTranslateDesktopHarness");
        Directory.CreateDirectory(testSettings);
        var sessionPath = Path.Combine(testSettings, "ime-test-session.json");
        var session = File.Exists(sessionPath) ? JsonSerializer.Deserialize<ImeTestSession>(File.ReadAllText(sessionPath)) : null;
        var credential = Environment.GetEnvironmentVariable("PINYINTRANSLATE_TEST_CREDENTIAL");
        if (string.IsNullOrWhiteSpace(credential)) credential = session?.CredentialPath;
        if (session?.Mock != true && (string.IsNullOrWhiteSpace(credential) || !File.Exists(credential)))
        { MessageBox.Show("测试凭据路径未提供或不存在。"); return; }
        var app = new Application();
        app.Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("pack://application:,,,/PinyinTranslate.App;component/Theme.xaml", UriKind.Absolute)
        });
        // Real development tests retain the original shared, capped task ledger.
        var logPath = Path.Combine(testSettings, "selection-diagnostics.log");
        void Diagnostic(string message) => ThreadPool.QueueUserWorkItem(_ =>
        {
            lock (DiagnosticLock) { try { File.AppendAllText(logPath, DateTimeOffset.UtcNow.ToString("O") + " " + message + Environment.NewLine); } catch (Exception) { } }
        });
        var taskLedgerPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PinyinTranslate", "api-budget.json");
        Func<string, CancellationToken, Task<string>>? mock = session?.Mock == true ? async (source, token) =>
        {
            // Reload only synthetic timing/failure controls, so the desktop test can
            // exercise waiting and cancellation without restarting the application.
            ImeTestSession controls = session;
            try { controls = JsonSerializer.Deserialize<ImeTestSession>(File.ReadAllText(sessionPath)) ?? session; }
            catch (IOException) { }
            catch (JsonException) { }
            await Task.Delay(Math.Clamp(controls.DelayMilliseconds, 0, 60000), token);
            if (controls.FailTranslations) throw new InvalidOperationException("模拟翻译请求失败。");
            return source switch { "是" => "is", "属于" => "belong to", "谁" => "who", "说" => "say", "你好" => "hello", _ => "mock: " + source };
        } : null;
        var services = new DesktopServices(() => string.IsNullOrWhiteSpace(credential) ? "" : SecretStore.Load(credential), testSettings,
            Diagnostic, new BudgetLedger(taskLedgerPath), mock, session?.TargetProcessId);
        services.StateChanged += (_, _) => Diagnostic("service-status " + services.Status);
        // Isolated shortcut set: do not compete with the user's running release.
        services.ApplySettingsAsync(services.Settings with { ImeHotkey = "Ctrl+Alt+F8", SelectionHotkey = "Ctrl+Alt+F9", ScreenshotHotkey = "Ctrl+Alt+F10" }).GetAwaiter().GetResult();
        var main = new MainWindow(services) { Title = session?.Mock == true ? "PinyinTranslate IME Test - mock" : "PinyinTranslate IME Test - DeepSeek" };
        if (session?.AutoEnable == true) main.Loaded += async (_, _) => await services.SetImeEnabledAsync(true);
        app.Run(main);
    }
    private sealed record ImeTestSession(int TargetProcessId, bool Mock = true, int DelayMilliseconds = 800,
        bool AutoEnable = true, string? CredentialPath = null, bool FailTranslations = false);
}
