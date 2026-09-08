using System.ComponentModel;
using System.Net;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using PenBridge.Input;
using PenBridge.Logging;
using PenBridge.Models;
using PenBridge.Networking;
using QRCoder;
using Forms = System.Windows.Forms;

namespace PenBridge.UI;

public partial class MainWindow : Window
{
    /// <summary>UI-thread-produced monitor inventory, published atomically for Kestrel readers.
    /// Remote selections are marshaled to the UI thread before this snapshot is replaced.</summary>
    private sealed record LiveSettings(MonitorRect Monitor, MonitorOption[] Monitors);
    private volatile LiveSettings _live = new(default, []);

    private readonly ILog _log = new FileLog();
    private readonly AppSettings _settings = AppSettings.Load();

    private PenBridgeServer? _server;
    private PointerInjector? _injector;
    private bool _closing;
    private bool _closeReady;
    private Task _serverOperation = Task.CompletedTask;

    private readonly SolidColorBrush _statusBrush = new((Color)ColorConverter.ConvertFromString("#9CA3AF")!);

    public MainWindow()
    {
        InitializeComponent();
        // Can't be set as a StaticResource on <Window> itself in XAML: a root element can't
        // resolve resources declared in its own Resources section, only descendants can.
        Background = (Brush)FindResource("WindowBg");
        string version = typeof(MainWindow).Assembly.GetName().Version?.ToString(3) ?? "?";
        Title = $"PenBridge v{version}";
        VersionText.Text = $"v{version}";
        StatusDotHost.Background = _statusBrush;

        var appIcon = LoadAppIcon();
        if (appIcon is not null)
        {
            Icon = appIcon;
            HeaderIcon.Source = appIcon;
        }

        RestoreWindowBounds();
        PopulateNetworkAdapters();
        PopulateMonitors();
        PortBox.Text = Math.Clamp(_settings.Port, 1, 65535).ToString();
        PortBox.PreviewTextInput += (_, e) => e.Handled = !e.Text.All(char.IsDigit);

        InitializeTray();
        _log.Logged += (level, message) => PostIfOpen(() =>
        {
            AppendLog(level, message);
            if (level == LogLevel.Error) NotifyError(message);
        });

        StartStopButton.Click += async (_, _) => await RunServerOperationAsync();
        CopyButton.Click += (_, _) => { if (UrlBox.Text.Length > 0) Clipboard.SetText(UrlBox.Text); };

        Closing += OnClosingAsync;
        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
        Closed += (_, _) =>
        {
            SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
            _tray.Visible = false; _tray.Dispose(); _trayMenu.Dispose();
        };
        StateChanged += (_, _) => { if (WindowState == WindowState.Minimized && !_closing) HideToTray(); };
    }

    private static BitmapSource? LoadAppIcon()
    {
        try
        {
            using var icon = System.Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath ?? System.Diagnostics.Process.GetCurrentProcess().MainModule!.FileName!);
            if (icon is null) return null;
            var bitmap = System.Windows.Interop.Imaging.CreateBitmapSourceFromHIcon(
                icon.Handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            bitmap.Freeze();
            return bitmap;
        }
        catch (Exception) { return null; }
    }

    private void OnDisplaySettingsChanged(object? sender, EventArgs e) => PostIfOpen(PopulateMonitors);

    private void PostIfOpen(Action action)
    {
        // Callers include Kestrel-thread events (ILog.Logged, PenBridgeServer.ConnectionStateChanged).
        // IsLoaded is a WPF instance member and calls VerifyAccess(), so it can only be read on the
        // UI thread — check it inside the dispatched callback, not here. Dispatcher itself is safe
        // to touch from any thread.
        if (_closing) return;
        try { Dispatcher.BeginInvoke(() => { if (!_closing && IsLoaded) action(); }); }
        catch (TaskCanceledException) { /* the window closed before the callback ran */ }
    }

    /// <summary>Restores the last normal (not minimized/maximized) window position and size, but
    /// only if it would still land on a currently connected monitor — otherwise a monitor removed
    /// since the last run could strand the window off-screen with no way to reach it.</summary>
    private void RestoreWindowBounds()
    {
        if (_settings is { WindowX: int x, WindowY: int y, WindowWidth: int w, WindowHeight: int h })
        {
            double width = Math.Max(w, MinWidth), height = Math.Max(h, MinHeight);
            bool OnScreen(Forms.Screen screen) => screen.WorkingArea.Right > x && screen.WorkingArea.Left < x + width
                && screen.WorkingArea.Bottom > y && screen.WorkingArea.Top < y + height;
            if (Forms.Screen.AllScreens.Any(OnScreen))
            {
                WindowStartupLocation = WindowStartupLocation.Manual;
                Left = x; Top = y; Width = width; Height = height;
                return;
            }
        }
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
    }

    /// <summary>Captures the current normal-state bounds into settings; called right before saving
    /// so a window that's minimized/maximized (or hidden to tray) still persists its restored size.</summary>
    private void SaveWindowBounds()
    {
        var bounds = WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds;
        _settings.WindowX = (int)bounds.X;
        _settings.WindowY = (int)bounds.Y;
        _settings.WindowWidth = (int)bounds.Width;
        _settings.WindowHeight = (int)bounds.Height;
    }

    private void PopulateNetworkAdapters()
    {
        var choices = NetworkAdapters.Enumerate();
        NetworkCombo.ItemsSource = choices;

        NetworkChoice? preferred = choices.FirstOrDefault(c => c.Address.ToString() == _settings.NetworkAddress)
            ?? NetworkAdapters.PickDefault(choices);
        if (preferred is not null)
            NetworkCombo.SelectedItem = preferred;

        if (choices.Count == 0)
            AppendLog(LogLevel.Error, "사용 가능한 네트워크 어댑터를 찾지 못했습니다. Wi-Fi 또는 이더넷 연결을 확인하세요.");
    }

    private void PopulateMonitors()
    {
        var screens = Forms.Screen.AllScreens;
        var monitors = screens.Select((s, i) => new MonitorOption(s.DeviceName,
            $"화면 {i + 1} · {s.Bounds.Width}×{s.Bounds.Height}{(s.Primary ? " · 주 모니터" : "")}",
            new MonitorRect(s.Bounds.Left, s.Bounds.Top, s.Bounds.Width, s.Bounds.Height))).ToArray();
        var selected = monitors.FirstOrDefault(m => m.Id == _settings.MonitorDeviceName)
            ?? monitors.First(m => m.Id == (Forms.Screen.PrimaryScreen ?? screens[0]).DeviceName);
        _settings.MonitorDeviceName = selected.Id;
        _live = new LiveSettings(selected.Bounds, monitors);
    }

    private Task<bool> SelectMonitorAsync(string id)
    {
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (_closing) return Task.FromResult(false);
        try { Dispatcher.BeginInvoke(() =>
        {
            if (_closing) { completion.TrySetResult(false); return; }
            var selected = _live.Monitors.FirstOrDefault(m => m.Id == id);
            if (selected is null) { completion.TrySetResult(false); return; }
            _settings.MonitorDeviceName = id;
            _live = new LiveSettings(selected.Bounds, _live.Monitors);
            _settings.Save();
            completion.TrySetResult(true);
        }); }
        catch (TaskCanceledException) { completion.TrySetResult(false); }
        return completion.Task;
    }

    private async Task ToggleServerAsync()
    {
        if (_server is null)
            await StartServerAsync();
        else
            await StopServerAsync();
    }

    private async Task StartServerAsync()
    {
        if (NetworkCombo.SelectedItem is not NetworkChoice adapter)
        {
            System.Windows.MessageBox.Show(this, "먼저 네트워크 어댑터를 선택하세요.", "PenBridge", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (!int.TryParse(PortBox.Text, out int port) || port is < 1 or > 65535)
        {
            System.Windows.MessageBox.Show(this, "포트는 1~65535 사이의 숫자여야 합니다.", "PenBridge", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        StartStopButton.IsEnabled = false;
        SetSetupControlsEnabled(false);

        _injector = new PointerInjector(_log);
        if (!_injector.TryOpen(out string? error))
        {
            _injector = null;
            SetSetupControlsEnabled(true);
            StartStopButton.IsEnabled = true;
            System.Windows.MessageBox.Show(this, $"펜 입력 장치를 열지 못해 서버를 시작할 수 없습니다.\n\n{error}",
                "PenBridge", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        PopulateMonitors();
        _server = new PenBridgeServer(_log, _injector, () => _live.Monitor,
            () => MappingMode.PreserveAspectRatio, () => false, () => _live.Monitors, SelectMonitorAsync);
        _server.ConnectionStateChanged += (state, detail) => PostIfOpen(() => OnConnectionStateChanged(state, detail));

        try
        {
            await _server.StartAsync(adapter.Address, port, CancellationToken.None);
            UpdatePairingDisplay();
            StartStopButton.Content = "서버 중지";

            _settings.NetworkAddress = adapter.Address.ToString();
            _settings.Port = _server.BoundPort;
            _settings.Save();
        }
        catch (Exception ex)
        {
            _log.Error($"서버 시작 실패: {ex.Message}");
            await _server.DisposeAsync();
            _server = null;
            _injector.Dispose();
            _injector = null;
            SetSetupControlsEnabled(true);
            System.Windows.MessageBox.Show(this, $"서버를 시작하지 못했습니다.\n\n{ex.Message}",
                "PenBridge", MessageBoxButton.OK, MessageBoxImage.Error);
        }

        StartStopButton.IsEnabled = true;
    }

    private async Task StopServerAsync()
    {
        StartStopButton.IsEnabled = false;
        if (_server is not null)
        {
            await _server.DisposeAsync();
            _server = null;
        }
        _injector?.Dispose();
        _injector = null;

        SetStatusVisual(ConnectionState.Stopped, null);
        UrlBox.Text = string.Empty;
        FadeQr(false);
        StartStopButton.Content = "서버 시작";
        SetSetupControlsEnabled(true);
        StartStopButton.IsEnabled = true;
    }

    private void SetSetupControlsEnabled(bool enabled)
    {
        NetworkCombo.IsEnabled = enabled;
        PortBox.IsEnabled = enabled;
    }

    private void UpdatePairingDisplay()
    {
        if (_server is null) return;
        string url = $"http://{_server.BoundAddress}:{_server.BoundPort}";
        UrlBox.Text = url;

        var qrGenerator = new QRCodeGenerator();
        var qrData = qrGenerator.CreateQrCode(url, QRCodeGenerator.ECCLevel.Q);
        var pngQr = new PngByteQRCode(qrData);
        byte[] png = pngQr.GetGraphic(8);

        var bitmap = new BitmapImage();
        using (var stream = new MemoryStream(png))
        {
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.StreamSource = stream;
            bitmap.EndInit();
        }
        bitmap.Freeze();
        QrImage.Source = bitmap;
        FadeQr(true);
    }

    private void FadeQr(bool visible)
    {
        var animation = new DoubleAnimation(visible ? 1 : 0, TimeSpan.FromMilliseconds(visible ? 260 : 140));
        QrCard.BeginAnimation(OpacityProperty, animation);
    }

    private void OnConnectionStateChanged(ConnectionState state, string? detail)
    {
        UpdateTrayState(state, detail);
        SetStatusVisual(state, detail);
    }

    private void SetStatusVisual(ConnectionState state, string? detail)
    {
        var (text, detailText, color, pulsing) = state switch
        {
            ConnectionState.Starting => ("시작 중...", "", "#F59E0B", true),
            ConnectionState.WaitingForIPad => ("iPad 연결 대기 중", "", "#F59E0B", true),
            ConnectionState.Connected => ("연결됨", detail ?? "", "#22C55E", false),
            ConnectionState.Error => ("오류", detail ?? "", "#EF4444", false),
            _ => ("중지됨", "", "#9CA3AF", false),
        };
        StatusText.Text = text;
        StatusDetailText.Text = detailText;
        _statusBrush.BeginAnimation(SolidColorBrush.ColorProperty,
            new ColorAnimation((Color)ColorConverter.ConvertFromString(color)!, TimeSpan.FromMilliseconds(220)));

        if (pulsing)
        {
            var pulse = new DoubleAnimation(1, 0.35, TimeSpan.FromMilliseconds(650)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever };
            StatusDotHost.BeginAnimation(OpacityProperty, pulse);
        }
        else
        {
            StatusDotHost.BeginAnimation(OpacityProperty, null);
            StatusDotHost.Opacity = 1;
            if (state == ConnectionState.Connected && StatusDotHost.RenderTransform is ScaleTransform scale)
            {
                var bounce = new DoubleAnimationUsingKeyFrames();
                bounce.KeyFrames.Add(new EasingDoubleKeyFrame(0.5, KeyTime.FromTimeSpan(TimeSpan.Zero)));
                bounce.KeyFrames.Add(new EasingDoubleKeyFrame(1.25, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(160)))
                    { EasingFunction = new BackEase { Amplitude = 0.6, EasingMode = EasingMode.EaseOut } });
                bounce.KeyFrames.Add(new EasingDoubleKeyFrame(1.0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(260))));
                scale.BeginAnimation(ScaleTransform.ScaleXProperty, bounce);
                scale.BeginAnimation(ScaleTransform.ScaleYProperty, bounce);
            }
        }
    }

    private void AppendLog(LogLevel level, string message)
    {
        string prefix = level switch { LogLevel.Error => "[ERROR]", LogLevel.Warn => "[WARN]", _ => "[INFO]" };
        LogBox.AppendText($"[{DateTime.Now:HH:mm:ss}] {prefix} {message}\r\n");
        LogBox.ScrollToEnd();
    }

    private async void OnClosingAsync(object? sender, CancelEventArgs e)
    {
        if (_closeReady) return;
        e.Cancel = true;
        if (!_exitRequested) { HideToTray(); return; }
        if (_closing) return;
        _closing = true;

        var progress = new ClosingWindow { Owner = this };
        progress.Show();
        IsEnabled = false;

        try
        {
            await Task.Yield();
            try { await _serverOperation; }
            catch (Exception ex) { _log.Error($"Server operation failed during shutdown: {ex.Message}"); }
            SaveWindowBounds();
            await Task.Run(() => _settings.Save());
            if (_server is not null)
            {
                await _server.DisposeAsync();
                _server = null;
            }
        }
        catch (Exception ex)
        {
            _log.Error($"Shutdown failed: {ex.Message}");
        }
        finally
        {
            _injector?.Dispose();
            _injector = null;
            _closeReady = true;
            progress.Close();
            Close();
        }
    }
}
