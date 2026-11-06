using System.Windows;

namespace PinyinTranslate.App;

/// <summary>Applies a palette to the application and each existing window without rebuilding their content.</summary>
internal static class ThemeManager
{
    public static bool IsSupported(string? theme) => theme is "light" or "pop" or "comic" or "collage" or "gold";

    public static string DisplayName(string? theme) => theme switch
    {
        "pop" => "波普风",
        "comic" => "漫画风",
        "collage" => "剪纸拼贴风",
        "gold" => "黑金鎏金风",
        _ => "极简风"
    };

    public static void Apply(string theme)
    {
        if (!IsSupported(theme)) throw new ArgumentException("未知的界面风格。", nameof(theme));
        var app = Application.Current ?? throw new InvalidOperationException("界面资源不可用。");
        if (!app.Dispatcher.CheckAccess()) throw new InvalidOperationException("界面风格必须在界面线程切换。");
        var uri = new Uri(theme switch
        {
            "pop" => "/PinyinTranslate.App;component/Palette.Pop.xaml",
            "comic" => "/PinyinTranslate.App;component/Palette.Comic.xaml",
            "collage" => "/PinyinTranslate.App;component/Palette.Collage.xaml",
            "gold" => "/PinyinTranslate.App;component/Palette.Gold.xaml",
            _ => "/PinyinTranslate.App;component/Palette.Light.xaml"
        }, UriKind.Relative);
        var windows = app.Windows.Cast<Window>().ToArray();
        // Load all dictionaries before touching the currently visible resources.
        var appPalette = new ResourceDictionary { Source = uri };
        var windowPalettes = windows.Select(_ => new ResourceDictionary { Source = uri }).ToArray();
        ReplacePalette(app.Resources, appPalette);
        for (int i = 0; i < windows.Length; i++)
            ReplacePalette(windows[i].Resources, windowPalettes[i]);
    }

    private static void ReplacePalette(ResourceDictionary target, ResourceDictionary palette)
    {
        var dictionaries = target.MergedDictionaries;
        for (int i = 0; i < dictionaries.Count; i++)
        {
            var source = dictionaries[i].Source?.OriginalString;
            if (source != null && source.Contains("Palette.", StringComparison.OrdinalIgnoreCase) &&
                source.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase))
            {
                dictionaries[i] = palette;
                return;
            }
        }
        dictionaries.Add(palette);
    }
}
