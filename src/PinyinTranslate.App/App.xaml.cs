using System.Windows;

namespace PinyinTranslate.App;

public partial class App : Application
{
    private Mutex? _singleInstance;
    /// <summary>Set before startup to connect production services.</summary>
    public static Func<IAppServices> ServicesFactory { get; set; } = () => new DesktopServices();
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        _singleInstance = new Mutex(true, @"Local\PinyinTranslate.SingleInstance", out bool created);
        if (!created)
        {
            _singleInstance.Dispose(); _singleInstance = null;
            MessageBox.Show("PinyinTranslate 已在运行。", "PinyinTranslate", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown(); return;
        }
        // Preserve an existing opt-in when moving to either managed distribution.
        try { StartupManager.RefreshEnabledForCurrentVersion(); } catch (Exception) { }
        var window = new MainWindow(ServicesFactory());
        MainWindow = window;
        window.Show();
    }
    protected override void OnExit(ExitEventArgs e)
    {
        if (_singleInstance != null) { _singleInstance.ReleaseMutex(); _singleInstance.Dispose(); }
        base.OnExit(e);
    }
}
