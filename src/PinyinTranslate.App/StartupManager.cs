using System.IO;
using System.Reflection;
using Microsoft.Win32;

namespace PinyinTranslate.App;

internal readonly record struct StartupState(bool Enabled, bool PointsToCurrentProgram);

/// <summary>Manages only this application's Run entry for the current Windows user.</summary>
internal static class StartupManager
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "PinyinTranslate";

    public static StartupState Read()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: false);
        object? value = key?.GetValue(ValueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
        if (value is not string command || string.IsNullOrWhiteSpace(command)) return new(false, false);
        string current = CurrentCommand();
        bool matches = string.Equals(command.Trim(), current, StringComparison.OrdinalIgnoreCase);
        return new(true, matches);
    }

    public static void SetEnabled(bool enabled)
    {
        if (!enabled)
        {
            using var existing = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            existing?.DeleteValue(ValueName, throwOnMissingValue: false);
            return;
        }

        string command = CurrentCommand();
        using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true)
            ?? throw new IOException("无法打开当前用户的启动项。");
        key.SetValue(ValueName, command, RegistryValueKind.String);
    }

    private static string CurrentCommand()
    {
        var entry = Assembly.GetEntryAssembly();
        if (!string.Equals(entry?.GetName().Name, "PinyinTranslate.App", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("当前运行环境无法设置自启动。");
        string program = Environment.ProcessPath ?? throw new InvalidOperationException("无法取得当前程序路径。");
        if (string.Equals(Path.GetFileNameWithoutExtension(program), "dotnet", StringComparison.OrdinalIgnoreCase))
        {
            string dll = entry?.Location ?? "";
            if (string.IsNullOrWhiteSpace(dll) || !File.Exists(dll))
                throw new InvalidOperationException("无法取得应用程序路径。");
            return Quote(program) + " " + Quote(dll);
        }
        return Quote(program);
    }

    private static string Quote(string path) => "\"" + path + "\"";
}
