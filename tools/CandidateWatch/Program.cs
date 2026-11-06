using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using PinyinTranslate.Input;

// The PID is mandatory and limited to our isolated synthetic fixture. This is a
// local diagnostic: it never writes logs, sends network requests, or injects input.
string? bridgePath = null;
var diagnosticArguments = new List<string>();
for (int index = 0; index < args.Length; index++)
{
    if (args[index] == "--bridge-path")
    {
        if (++index >= args.Length || bridgePath is not null) { Console.Error.WriteLine("--bridge-path requires one executable path."); return 2; }
        bridgePath = Path.GetFullPath(args[index]);
    }
    else diagnosticArguments.Add(args[index]);
}
args = diagnosticArguments.ToArray();
bool isolated = args.Contains("--isolated") || bridgePath is not null;
args = args.Where(argument => argument != "--isolated").ToArray();
if (args.Length is < 1 or > 2 || !int.TryParse(args[0], out int pid) || pid <= 0 ||
    (args.Length == 2 && (!int.TryParse(args[1], out int secondsArg) || secondsArg is < 1 or > 600)))
{
    Console.Error.WriteLine("用法：CandidateWatch <DesktopFixture 进程 ID> [持续秒数 1–600，默认 60]");
    return 2;
}
int seconds = args.Length == 2 ? int.Parse(args[1]) : 60;
Process? fixture = null;
try
{
    fixture = Process.GetProcessById(pid);
    // Retain the original process handle before inspecting its identity.
    _ = fixture.Handle;
    if (fixture.HasExited || !string.Equals(fixture.ProcessName, "DesktopFixture", StringComparison.OrdinalIgnoreCase) ||
        !IsProjectFixture(fixture.MainModule?.FileName) || fixture.HasExited)
    {
        fixture.Dispose();
        Console.Error.WriteLine("只允许监视本项目的 DesktopFixture 合成样例进程。");
        return 2;
    }
}
catch (Exception e) when (e is ArgumentException or IOException or InvalidOperationException or System.ComponentModel.Win32Exception or UnauthorizedAccessException)
{
    fixture?.Dispose();
    Console.Error.WriteLine("无法验证样例进程：" + e.GetType().Name);
    return 2;
}

Console.OutputEncoding = Encoding.UTF8;
using var fixtureProcess = fixture!;
using var cancellation = new CancellationTokenSource();
ConsoleCancelEventHandler cancel = (_, e) => { e.Cancel = true; cancellation.Cancel(); };
Console.CancelKeyPress += cancel;
try
{
    using (ICandidateObserver observer = isolated ? new CandidateObserverClient(fixtureProcess, "FixtureText", bridgePath: bridgePath) : new CandidateObserver(fixtureProcess, "FixtureText"))
    {
    long invalidations = 0;
    observer.Status += status => Console.WriteLine(JsonSerializer.Serialize(new { kind = "status", status }));
    observer.Invalidated += _ => Interlocked.Increment(ref invalidations);
    observer.Observed += observation => { _ = ReportObservationAsync(observation); };
    async Task ReportObservationAsync(CandidateObservation observation)
    {
        try
        {
            if (observation.FocusProcessId != pid || fixtureProcess.HasExited || !observer.IsCurrent(observation.Version) ||
                !await observer.ValidateAsync(observation, cancellation.Token) || fixtureProcess.HasExited || !observer.IsCurrent(observation.Version)) return;
            // The candidate window's session ownership is unproven. Never print content or content-derived labels.
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                kind = "candidate-metadata", observation.Version, observation.FocusProcessId,
                observation.Bounds
            }, new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
        }
        catch (Exception) { }
    }
    try
    {
        if (!await observer.Ready.WaitAsync(TimeSpan.FromSeconds(5), cancellation.Token))
        {
            Console.Error.WriteLine("候选观察器未能启动。");
            return 3;
        }
    }
    catch (TimeoutException) { Console.Error.WriteLine("候选观察器启动超时。"); return 3; }
    catch (OperationCanceledException) { return 0; }
    if (!observer.SetEnabled(true)) { Console.Error.WriteLine("候选观察器不可用。"); return 3; }
    Console.WriteLine(JsonSerializer.Serialize(new { kind = "started", fixturePid = pid, seconds }));
    try
    {
        var duration = Task.Delay(TimeSpan.FromSeconds(seconds), cancellation.Token);
        var exited = fixtureProcess.WaitForExitAsync(cancellation.Token);
        await await Task.WhenAny(duration, exited);
    }
    catch (OperationCanceledException) { }
    observer.SetEnabled(false);
    cancellation.Cancel();
    Console.WriteLine(JsonSerializer.Serialize(new { kind = "invalidations", count = Interlocked.Read(ref invalidations) }));
    }
    Console.WriteLine("{\"kind\":\"finished\"}");
}
catch (Exception e) when (e is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception or UnauthorizedAccessException)
{
    Console.Error.WriteLine("候选诊断无法继续：" + e.GetType().Name);
    return 3;
}
finally { Console.CancelKeyPress -= cancel; }
return 0;

static bool IsProjectFixture(string? executable)
{
    if (string.IsNullOrWhiteSpace(executable) || !string.Equals(Path.GetFileName(executable), "DesktopFixture.exe", StringComparison.OrdinalIgnoreCase))
        return false;
    DirectoryInfo? directory = new(AppContext.BaseDirectory);
    string? root = null;
    for (int i = 0; directory != null && i < 12; i++, directory = directory.Parent)
    {
        if (File.Exists(Path.Combine(directory.FullName, "tools", "DesktopFixture", "DesktopFixture.csproj")) &&
            File.Exists(Path.Combine(directory.FullName, "src", "PinyinTranslate.Input", "PinyinTranslate.Input.csproj")))
        { root = directory.FullName; break; }
    }
    if (root is null) return false;
    string allowedDirectory = Path.GetFullPath(Path.Combine(root, "tools", "DesktopFixture", "bin"));
    string path = Path.GetFullPath(executable);
    string relative = Path.GetRelativePath(allowedDirectory, path);
    if (Path.IsPathRooted(relative) || relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)) return false;
    var file = new FileInfo(path);
    if (!file.Exists || (file.Attributes & FileAttributes.ReparsePoint) != 0) return false;
    for (DirectoryInfo? parent = file.Directory; parent != null; parent = parent.Parent)
    {
        if ((parent.Attributes & FileAttributes.ReparsePoint) != 0) return false;
        if (string.Equals(parent.FullName.TrimEnd(Path.DirectorySeparatorChar), allowedDirectory.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
            return true;
    }
    return false;
}
