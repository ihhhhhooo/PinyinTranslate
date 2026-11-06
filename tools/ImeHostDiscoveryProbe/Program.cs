using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using FlaUI.UIA3;

internal static class Program
{
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private delegate bool EnumWindowCallback(nint window, nint parameter);
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowCallback callback, nint parameter);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(nint window, StringBuilder className, int capacity);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint pid);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsWindowVisible(nint window);
    [DllImport("user32.dll")] private static extern nint GetDesktopWindow();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint FindWindowEx(nint parent, nint after, string className, string? windowName);

    [STAThread]
    private static int Main(string[] args)
    {
        if (!args.Contains("--metadata-only") || args.Any(argument => argument is not ("--metadata-only" or "--wpf-initialized" or "--uia2-selection-subscription")))
        {
            Console.Error.WriteLine("Usage: ImeHostDiscoveryProbe --metadata-only [--wpf-initialized] [--uia2-selection-subscription]");
            return 2;
        }
#if !LEGACY_UIA2_PROBE
        if (args.Contains("--uia2-selection-subscription"))
        {
            Console.Error.WriteLine("Build with -p:IncludeLegacyUia2Probe=true to enable the isolated UIA2 registration experiment.");
            return 2;
        }
#endif
        bool initializeWpf = args.Contains("--wpf-initialized");
        System.Windows.Application? application = initializeWpf ? new() { ShutdownMode = System.Windows.ShutdownMode.OnExplicitShutdown } : null;
#if LEGACY_UIA2_PROBE
        using var subscribed = new ManualResetEventSlim(false);
        using var stopSubscription = new ManualResetEventSlim(false);
        Thread? selectionWorker = null;
        if (args.Contains("--uia2-selection-subscription"))
        {
            selectionWorker = new Thread(() => RegisterLegacySelection(subscribed, stopSubscription)) { IsBackground = true };
            selectionWorker.SetApartmentState(ApartmentState.MTA);
            selectionWorker.Start();
            if (!subscribed.Wait(TimeSpan.FromSeconds(3))) Print(new { stage = "uia2-selection-subscription", registered = false, reason = "registration-timeout" });
        }
#endif
        int result = 4;
        var worker = new Thread(() => result = Probe(initializeWpf));
        worker.SetApartmentState(ApartmentState.MTA);
        worker.Start();
        worker.Join();
#if LEGACY_UIA2_PROBE
        stopSubscription.Set();
        selectionWorker?.Join(TimeSpan.FromSeconds(2));
#endif
        application?.Shutdown();
        return result;
    }

#if LEGACY_UIA2_PROBE
    private static void RegisterLegacySelection(ManualResetEventSlim ready, ManualResetEventSlim stop)
    {
        System.Windows.Automation.AutomationElement? root = null;
        System.Windows.Automation.AutomationEventHandler handler = (_, _) => { }; // Never read the selection payload or text.
        bool registered = false;
        try
        {
            root = System.Windows.Automation.AutomationElement.RootElement;
            System.Windows.Automation.Automation.AddAutomationEventHandler(
                System.Windows.Automation.TextPattern.TextSelectionChangedEvent, root,
                System.Windows.Automation.TreeScope.Subtree, handler);
            registered = true;
            Print(new { stage = "uia2-selection-subscription", registered });
            ready.Set();
            stop.Wait();
        }
        catch (Exception error) { Print(new { stage = "uia2-selection-subscription", registered, error = Failure(error) }); }
        finally
        {
            ready.Set();
            if (registered && root is not null)
                try { System.Windows.Automation.Automation.RemoveAutomationEventHandler(System.Windows.Automation.TextPattern.TextSelectionChangedEvent, root, handler); }
                catch (Exception error) { Print(new { stage = "uia2-selection-unsubscribe", error = Failure(error) }); }
        }
    }
#endif

    private static int Probe(bool wpfInitialized)
    {
        try
        {
            var processIds = new HashSet<int>();
            foreach (var process in Process.GetProcessesByName("TextInputHost"))
                using (process) processIds.Add(process.Id);
            var frames = new List<nint>();
            var buffer = new StringBuilder(256);
            EnumWindowCallback callback = (window, _) =>
            {
                buffer.Clear();
                if (GetClassName(window, buffer, buffer.Capacity) > 0 && buffer.ToString() == "ApplicationFrameWindow") frames.Add(window);
                return true;
            };
            bool enumerated = EnumWindows(callback, 0);
            GC.KeepAlive(callback);
            var findFrames = FindFrames(0);
            var desktopFindFrames = FindFrames(GetDesktopWindow());
            Print(new { stage = "find-window-ex", nullParentFrames = findFrames.Count, desktopParentFrames = desktopFindFrames.Count });
            frames = frames.Concat(findFrames).Concat(desktopFindFrames).Distinct().ToList();
            nint foreground = GetForegroundWindow();
            uint foregroundThread = GetWindowThreadProcessId(foreground, out uint foregroundPid);
            Print(new { stage = "native-enumeration", wpfInitialized, sameAppManifest = true, nativeEnumOk = enumerated,
                frameCount = frames.Count, textInputProcessCount = processIds.Count, foregroundPid, foregroundThread,
                foregroundHwnd = Hex(foreground) });

            using var automation = new UIA3Automation();
            try
            {
                var desktopFrames = automation.GetDesktop().FindAllChildren(condition => condition.ByClassName("ApplicationFrameWindow"));
                Print(new { stage = "desktop-uia-enumeration", frameCount = desktopFrames.Length });
                foreach (var desktopFrame in desktopFrames)
                {
                    nint frameWindow = desktopFrame.Properties.NativeWindowHandle.ValueOrDefault;
                    buffer.Clear(); if (frameWindow != 0) GetClassName(frameWindow, buffer, buffer.Capacity);
                    Print(new { stage = "desktop-uia-frame", uiaClass = desktopFrame.Properties.ClassName.ValueOrDefault,
                        nativeHwndSupported = desktopFrame.Properties.NativeWindowHandle.IsSupported,
                        nativeHwnd = Hex(frameWindow), nativeClass = buffer.ToString(),
                        framePid = desktopFrame.Properties.ProcessId.ValueOrDefault });
                    var directChildren = desktopFrame.FindAllChildren(condition => condition.ByClassName("Windows.UI.Core.CoreWindow"));
                    foreach (var child in directChildren)
                    {
                        if (!child.Properties.ProcessId.IsSupported || !processIds.Contains(child.Properties.ProcessId.ValueOrDefault)) continue;
                        nint childWindow = child.Properties.NativeWindowHandle.ValueOrDefault;
                        buffer.Clear(); if (childWindow != 0) GetClassName(childWindow, buffer, buffer.Capacity);
                        Print(new { stage = "desktop-uia-core", hostPid = child.Properties.ProcessId.ValueOrDefault,
                            uiaClass = child.Properties.ClassName.ValueOrDefault,
                            nativeHwndSupported = child.Properties.NativeWindowHandle.IsSupported,
                            nativeHwnd = Hex(childWindow), nativeClass = buffer.ToString() });
                    }
                }
            }
            catch (Exception error) { Print(new { stage = "desktop-uia-enumeration", error = Failure(error) }); }

            var nativeCores = FindFrames(0, "Windows.UI.Core.CoreWindow").Concat(FindFrames(GetDesktopWindow(), "Windows.UI.Core.CoreWindow")).Distinct().ToArray();
            foreach (nint coreWindow in nativeCores)
            {
                GetWindowThreadProcessId(coreWindow, out uint corePid);
                if (!processIds.Contains((int)corePid)) continue;
                try
                {
                    var core = automation.FromHandle(coreWindow);
                    var menus = core.FindAllDescendants(condition => condition.ByAutomationId("IME_Candidate_Window"));
                    Print(new { stage = "direct-native-core", hwnd = Hex(coreWindow), corePid, mapped = true,
                        uiaClass = core.Properties.ClassName.ValueOrDefault, candidateMenuCount = menus.Length,
                        visibleCandidateMenus = menus.Count(menu => menu.Properties.IsOffscreen.IsSupported && !menu.Properties.IsOffscreen.ValueOrDefault) });
                }
                catch (Exception error) { Print(new { stage = "direct-native-core", hwnd = Hex(coreWindow), corePid, mapped = false, error = Failure(error) }); }
            }

            int totalHosts = 0;
            foreach (nint window in frames)
            {
                buffer.Clear(); GetClassName(window, buffer, buffer.Capacity);
                string nativeClass = buffer.ToString();
                uint frameThread = GetWindowThreadProcessId(window, out uint framePid);
                Print(new { stage = "native-frame", hwnd = Hex(window), nativeClass, framePid, frameThread, visible = IsWindowVisible(window) });
                var childCores = FindFrames(window, "Windows.UI.Core.CoreWindow");
                Print(new { stage = "native-frame-core-lookup", frameHwnd = Hex(window), nativeChildCoreCount = childCores.Count });
                foreach (nint childWindow in childCores)
                {
                    GetWindowThreadProcessId(childWindow, out uint childPid);
                    if (!processIds.Contains((int)childPid)) continue;
                    try
                    {
                        var child = automation.FromHandle(childWindow);
                        var menus = child.FindAllDescendants(condition => condition.ByAutomationId("IME_Candidate_Window"));
                        Print(new { stage = "native-child-core-provider", frameHwnd = Hex(window), hwnd = Hex(childWindow), childPid,
                            candidateMenuCount = menus.Length, visibleCandidateMenus = menus.Count(menu => menu.Properties.IsOffscreen.IsSupported && !menu.Properties.IsOffscreen.ValueOrDefault) });
                    }
                    catch (Exception error) { Print(new { stage = "native-child-core-provider", hwnd = Hex(childWindow), error = Failure(error) }); }
                }
                try
                {
                    var frame = automation.FromHandle(window);
                    var children = frame.FindAllChildren(condition => condition.ByClassName("Windows.UI.Core.CoreWindow"));
                    int hosts = 0;
                    foreach (var child in children)
                    {
                        if (!child.Properties.ProcessId.IsSupported || !processIds.Contains(child.Properties.ProcessId.ValueOrDefault)) continue;
                        hosts++;
                        int runtimeIdLength = child.Properties.RuntimeId.IsSupported ? (child.Properties.RuntimeId.ValueOrDefault?.Length ?? 0) : 0;
                        Print(new { stage = "native-frame-host", frameHwnd = Hex(window), hostPid = child.Properties.ProcessId.ValueOrDefault,
                            className = child.Properties.ClassName.ValueOrDefault, runtimeIdLength,
                            nativeHwnd = Hex(child.Properties.NativeWindowHandle.ValueOrDefault) });
                    }
                    totalHosts += hosts;
                    Print(new { stage = "from-handle-result", frameHwnd = Hex(window), mapped = true, directCoreWindows = children.Length, textInputHosts = hosts });
                }
                catch (Exception error) { Print(new { stage = "from-handle-result", frameHwnd = Hex(window), mapped = false, error = Failure(error) }); }
            }
            Print(new { stage = "finished", totalHosts, note = "Metadata only; no names, text, candidate values, activation or input read/sent." });
            return totalHosts > 0 ? 0 : 4;
        }
        catch (Exception error) { Print(new { stage = "probe-error", error = Failure(error) }); return 5; }
    }

    private static string Hex(nint value) => "0x" + ((nuint)value).ToString("X");
    private static List<nint> FindFrames(nint parent, string className = "ApplicationFrameWindow")
    {
        var frames = new List<nint>();
        nint after = 0;
        while (frames.Count < 256)
        {
            nint current = FindWindowEx(parent, after, className, null);
            if (current == 0 || frames.Contains(current)) break;
            frames.Add(current);
            after = current;
        }
        return frames;
    }
    private static object Failure(Exception error) => new { type = error.GetType().Name, hresult = "0x" + error.HResult.ToString("X8") };
    private static void Print<T>(T value) => Console.WriteLine(JsonSerializer.Serialize(value));
}
