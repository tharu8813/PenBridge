using PenBridge.Input;
using PenBridge.Logging;
using PenBridge.Models;
using PenBridge.Networking;
using PenBridge.UI;
using Forms = System.Windows.Forms;

namespace PenBridge;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        // Diagnostics must use the same physical-pixel DPI context as the UI. This still applies
        // process-wide DPI awareness (PerMonitorV2, via ApplicationHighDpiMode) even though the
        // window itself is now WPF, not WinForms.
        ApplicationConfiguration.Initialize();
        if (args.Contains("preview-server"))
        {
            var log = new ConsoleLog();
            using var injector = new PointerInjector(log);
            var bounds = Forms.Screen.PrimaryScreen!.Bounds;
            var monitor = new MonitorRect(bounds.Left, bounds.Top, bounds.Width, bounds.Height);
            var server = new PenBridgeServer(log, injector, () => monitor,
                () => MappingMode.PreserveAspectRatio, () => false);
            server.StartAsync(System.Net.IPAddress.Loopback, 18080, CancellationToken.None).GetAwaiter().GetResult();
            File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "preview-url.txt"),
                $"http://127.0.0.1:{server.BoundPort}/");
            // Bounded local preview for video/UI checks; no native pointer device is opened.
            Thread.Sleep(TimeSpan.FromMinutes(5));
            server.DisposeAsync().AsTask().GetAwaiter().GetResult();
            return;
        }
        if (args.Contains("smoketest-injector"))
        {
            RunInjectorSmokeTest();
            return;
        }
        if (args.Contains("smoketest-monitors"))
        {
            RunMonitorDiagnostics();
            return;
        }

        if (!Environment.Is64BitProcess)
        {
            System.Windows.MessageBox.Show(
                "PenBridge는 64비트 Windows에서만 실행할 수 있습니다.",
                "PenBridge", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
            return;
        }

        if (!PlatformSupport.IsSyntheticPointerInputSupported)
        {
            System.Windows.MessageBox.Show(
                "PenBridge는 Windows 10 버전 1809 이상이 필요합니다.\n" +
                "현재 실행 중인 Windows 버전에서는 사용할 수 없습니다.",
                "PenBridge", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
            return;
        }

        new System.Windows.Application().Run(new MainWindow());
    }

    /// <summary>
    /// Exercises the real CreateSyntheticPointerDevice/InjectSyntheticPointerInput calls against
    /// the live OS — something no unit test can do, since there is no fake for a Win32 device
    /// handle. Run with: PenBridge.exe smoketest-injector. Injects one harmless down+up at the
    /// primary monitor's center so nothing is left in a pressed state.
    /// </summary>
    private static void RunInjectorSmokeTest()
    {
        Console.WriteLine($"64-bit process: {Environment.Is64BitProcess}");
        Console.WriteLine($"OS version: {Environment.OSVersion.VersionString}");
        Console.WriteLine($"Synthetic Pointer Input supported: {PlatformSupport.IsSyntheticPointerInputSupported}");

        var log = new ConsoleLog();
        using var injector = new PointerInjector(log);

        if (!injector.TryOpen(out string? error))
        {
            Console.WriteLine($"FAIL: CreateSyntheticPointerDevice — {error}");
            Environment.ExitCode = 1;
            return;
        }
        Console.WriteLine("OK: CreateSyntheticPointerDevice succeeded.");

        var monitor = new MonitorRect(Forms.Screen.PrimaryScreen!.Bounds.Left, Forms.Screen.PrimaryScreen.Bounds.Top,
            Forms.Screen.PrimaryScreen.Bounds.Width, Forms.Screen.PrimaryScreen.Bounds.Height);

        bool downOk = injector.Inject(new PenSample(PenPhase.Down, true, 0.5, 0.5, 0.3, 0, 0), monitor);
        bool upOk = injector.Inject(new PenSample(PenPhase.Up, false, 0.5, 0.5, 0.0, 0, 0), monitor);

        Console.WriteLine($"{(downOk ? "OK" : "FAIL")}: InjectSyntheticPointerInput (down)");
        Console.WriteLine($"{(upOk ? "OK" : "FAIL")}: InjectSyntheticPointerInput (up)");

        // Proves the API server can be constructed without any embedded web resources.
        try
        {
            _ = new PenBridgeServer(log, injector,
                () => monitor, () => MappingMode.PreserveAspectRatio, () => false);
            Console.WriteLine("OK: API server constructed without embedded website files.");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"FAIL: API server construction — {ex.Message}");
            downOk = upOk = false;
        }

        Environment.ExitCode = downOk && upOk ? 0 : 1;
    }

    /// <summary>Prints each Screen.AllScreens entry plus the overall virtual desktop bounds, to
    /// check for the classic dual-monitor failure modes: negative offsets, gaps/overlaps between
    /// monitors, or a virtual screen size that doesn't match the union of the individual bounds.</summary>
    private static void RunMonitorDiagnostics()
    {
        Console.WriteLine($"Screen.AllScreens.Length = {Forms.Screen.AllScreens.Length}");
        for (int i = 0; i < Forms.Screen.AllScreens.Length; i++)
        {
            var s = Forms.Screen.AllScreens[i];
            Console.WriteLine($"[{i}] DeviceName={s.DeviceName} Primary={s.Primary} Bounds={s.Bounds} WorkingArea={s.WorkingArea}");
        }
        Console.WriteLine($"SystemInformation.VirtualScreen = {Forms.SystemInformation.VirtualScreen}");
    }

    private sealed class ConsoleLog : ILog
    {
        // ILog requires this for the UI's live log panel; the CLI smoke test has no
        // UI to notify, so it's intentionally never raised.
#pragma warning disable CS0067
        public event Action<LogLevel, string>? Logged;
#pragma warning restore CS0067
        public void Info(string message) => Console.WriteLine($"[INFO] {message}");
        public void Warn(string message) => Console.WriteLine($"[WARN] {message}");
        public void Error(string message) => Console.WriteLine($"[ERROR] {message}");
    }
}
