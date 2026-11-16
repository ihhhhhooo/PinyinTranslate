using Velopack;
using PinyinTranslate.Selection;

namespace PinyinTranslate.App;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Length == 1 && args[0] == CopyClipboardWorker.Argument) return CopyClipboardWorker.Run();
        // Updater lifecycle arguments must exit before the mutex, WPF or input services exist.
        VelopackApp.Build().SetAutoApplyOnStartup(false).Run();
        var app = new App();
        app.InitializeComponent();
        return app.Run();
    }
}
