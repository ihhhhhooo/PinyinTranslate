using Velopack;

namespace PinyinTranslate.App;

internal static class Program
{
    [STAThread]
    public static void Main()
    {
        // Updater lifecycle arguments must exit before the mutex, WPF or input services exist.
        VelopackApp.Build().SetAutoApplyOnStartup(false).Run();
        var app = new App();
        app.InitializeComponent();
        app.Run();
    }
}
