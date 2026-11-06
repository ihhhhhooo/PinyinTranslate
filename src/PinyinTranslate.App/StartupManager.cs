using System.IO;
using System.Reflection;
using Microsoft.Win32;
using PinyinTranslate.Update;
using Velopack.Locators;

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

    public static void RefreshEnabledForCurrentVersion()
    {
        if (!HasManagedIdentity()) return;
        string program = Environment.ProcessPath ?? "";
        if (!Path.IsPathFullyQualified(program)) return;
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
        if (key?.GetValue(ValueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames) is not string previous) return;
        string current = CurrentCommand();
        if (StartupCommandPolicy.ShouldRelocate(previous, current))
            key.SetValue(ValueName, current, RegistryValueKind.String);
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
        return Quote(StartupCommandPolicy.FindManagedLauncher(program, HasManagedIdentity(), File.Exists));
    }

    private static bool HasManagedIdentity()
    {
        var locator = VelopackLocator.Current;
        return locator.AppId == VelopackBackend.PackageId && locator.Channel == VelopackBackend.Channel &&
               locator.CurrentlyInstalledVersion != null;
    }

    private static string Quote(string path) => "\"" + path + "\"";
}

/// <summary>Pure path/command policy; never touches the registry or launches a process.</summary>
internal static class StartupCommandPolicy
{
    public static string FindManagedLauncher(string program, bool managed, Func<string, bool> fileExists)
    {
        if (!managed || !Path.IsPathFullyQualified(program) || !string.Equals(Path.GetFileName(program), "PinyinTranslate.App.exe", StringComparison.OrdinalIgnoreCase)) return program;
        var directory = Path.GetDirectoryName(program);
        if (!string.Equals(Path.GetFileName(directory), "current", StringComparison.OrdinalIgnoreCase)) return program;
        var root = Path.GetDirectoryName(directory);
        if (string.IsNullOrWhiteSpace(root)) return program;
        string launcher = Path.Combine(root, "PinyinTranslate.exe");
        return fileExists(Path.Combine(root, "Update.exe")) && fileExists(launcher) ? launcher : program;
    }

    public static bool ShouldRelocate(string? previousCommand, string currentCommand)
    {
        if (string.IsNullOrWhiteSpace(previousCommand)) return false;
        string previous = previousCommand.Trim();
        if (string.Equals(previous, currentCommand, StringComparison.OrdinalIgnoreCase) ||
            previous.Length < 3 || previous[0] != '"' || previous[^1] != '"') return false;
        string executable = previous[1..^1];
        if (executable.Contains('"') || !Path.IsPathFullyQualified(executable)) return false;
        string name = Path.GetFileName(executable);
        return string.Equals(name, "PinyinTranslate.App.exe", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(name, "PinyinTranslate.exe", StringComparison.OrdinalIgnoreCase);
    }
}
