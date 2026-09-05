using System.Net;
using Microsoft.Win32;
using PenBridge.Input;
using PenBridge.Logging;
using PenBridge.Models;
using PenBridge.Networking;
using QRCoder;

namespace PenBridge.UI;

public partial class MainForm : Form
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

    // Setup controls
    private readonly ComboBox _networkCombo = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 260 };
    private readonly NumericUpDown _portInput = new() { Minimum = 1, Maximum = 65535, Value = 8080, Width = 80 };
    private readonly Button _startStopButton = new() { Text = "서버 시작", Width = 120 };

    // Connection controls
    private readonly Label _connectionStatusLabel = new() { AutoSize = true, Font = new Font(FontFamily.GenericSansSerif, 10, FontStyle.Bold), Text = "● 중지됨", ForeColor = Color.Gray };
    private readonly TextBox _urlBox = new() { ReadOnly = true, Width = 320 };
    private readonly Button _copyButton = new() { Text = "복사", Width = 60 };
    private readonly PictureBox _qrBox = new() { Width = 160, Height = 160, SizeMode = PictureBoxSizeMode.Zoom, BorderStyle = BorderStyle.FixedSingle };

    private readonly TextBox _log_box = new()
    {
        Multiline = true,
        ReadOnly = true,
        ScrollBars = ScrollBars.Vertical,
        Dock = DockStyle.Fill,
        Font = new Font(FontFamily.GenericMonospace, 9),
    };

    public MainForm()
    {
        string version = typeof(MainForm).Assembly.GetName().Version?.ToString(3) ?? "?";
        Text = $"PenBridge v{version}";
        Width = 620;
        Height = 640;
        MinimumSize = new Size(560, 500);

        BuildLayout();
        PopulateNetworkAdapters();
        PopulateMonitors();
        _portInput.Value = Math.Clamp(_settings.Port, _portInput.Minimum, _portInput.Maximum);

        InitializeTray();
        _log.Logged += (level, message) => PostIfOpen(() =>
        {
            AppendLog(level, message);
            if (level == LogLevel.Error) NotifyError(message);
        });

        _startStopButton.Click += async (_, _) =>
        {
            await RunServerOperationAsync();
        };
        _copyButton.Click += (_, _) => { if (_urlBox.Text.Length > 0) Clipboard.SetText(_urlBox.Text); };

        FormClosing += OnClosingAsync;
        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
        FormClosed += (_, _) =>
        {
            SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
            _tray.Visible = false; _tray.Dispose(); _trayMenu.Dispose();
        };
        Resize += (_, _) => { if (WindowState == FormWindowState.Minimized && !_closing) HideToTray(); };
    }

    private void OnDisplaySettingsChanged(object? sender, EventArgs e)
    {
        PostIfOpen(PopulateMonitors);
    }

    private void PostIfOpen(Action action)
    {
        if (_closing || IsDisposed || !IsHandleCreated) return;
        try { BeginInvoke(() => { if (!_closing && !IsDisposed) action(); }); }
        catch (InvalidOperationException) { /* The window closed before the callback was queued. */ }
    }

    private void BuildLayout()
    {
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 4,
            Padding = new Padding(12),
        };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        Controls.Add(root);

        // --- Setup group ---
        var setupGroup = new GroupBox { Text = "설정", Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(8) };
        var setupTable = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, Dock = DockStyle.Top };
        setupTable.Controls.Add(new Label { Text = "네트워크:", AutoSize = true, TextAlign = ContentAlignment.MiddleLeft, Margin = new Padding(3, 8, 3, 3) }, 0, 0);
        setupTable.Controls.Add(_networkCombo, 1, 0);
        setupTable.Controls.Add(new Label { Text = "포트:", AutoSize = true, Margin = new Padding(3, 8, 3, 3) }, 0, 1);
        setupTable.Controls.Add(_portInput, 1, 1);
        setupTable.Controls.Add(new Label { Text = "대상 모니터·입력·화면 설정은 iPad에서 변경하세요.", AutoSize = true, MaximumSize = new Size(420, 0) }, 1, 2);
        setupTable.Controls.Add(_startStopButton, 1, 3);
        setupTable.Controls.Add(new Label { Text = "창을 닫으면 트레이로 숨겨집니다. 완전 종료는 트레이 메뉴를 이용하세요.", AutoSize = true, MaximumSize = new Size(420, 0) }, 1, 4);
        setupGroup.Controls.Add(setupTable);
        root.Controls.Add(setupGroup, 0, 0);

        // --- Connect group ---
        var connectGroup = new GroupBox { Text = "iPad 연결", Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(8) };
        var connectLayout = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, Dock = DockStyle.Top };
        var rightColumn = new TableLayoutPanel { ColumnCount = 1, AutoSize = true };
        rightColumn.Controls.Add(_connectionStatusLabel);
        var urlRow = new FlowLayoutPanel { AutoSize = true, WrapContents = false };
        urlRow.Controls.Add(_urlBox);
        urlRow.Controls.Add(_copyButton);
        rightColumn.Controls.Add(urlRow);
        rightColumn.Controls.Add(new Label { Text = "공용 PenBridge 사이트에서 이 주소를 입력하거나 QR을 스캔하세요.", AutoSize = true, MaximumSize = new Size(330, 0), ForeColor = Color.DimGray });
        connectLayout.Controls.Add(_qrBox, 0, 0);
        connectLayout.Controls.Add(rightColumn, 1, 0);
        connectGroup.Controls.Add(connectLayout);
        root.Controls.Add(connectGroup, 0, 1);

        // --- Log group ---
        var logGroup = new GroupBox { Text = "진단 / 로그", Dock = DockStyle.Fill, Padding = new Padding(8) };
        logGroup.Controls.Add(_log_box);
        root.Controls.Add(logGroup, 0, 3);
    }

    private void PopulateNetworkAdapters()
    {
        var choices = NetworkAdapters.Enumerate();
        _networkCombo.DataSource = choices.ToList();
        _networkCombo.DisplayMember = nameof(NetworkChoice.DisplayName);

        NetworkChoice? preferred = choices.FirstOrDefault(c => c.Address.ToString() == _settings.NetworkAddress)
            ?? NetworkAdapters.PickDefault(choices);
        if (preferred is not null)
            _networkCombo.SelectedItem = preferred;

        if (choices.Count == 0)
            AppendLog(LogLevel.Error, "사용 가능한 네트워크 어댑터를 찾지 못했습니다. Wi-Fi 또는 이더넷 연결을 확인하세요.");
    }

    private void PopulateMonitors()
    {
        var screens = Screen.AllScreens;
        var monitors = screens.Select((s, i) => new MonitorOption(s.DeviceName,
            $"화면 {i + 1} · {s.Bounds.Width}×{s.Bounds.Height}{(s.Primary ? " · 주 모니터" : "")}",
            new MonitorRect(s.Bounds.Left, s.Bounds.Top, s.Bounds.Width, s.Bounds.Height))).ToArray();
        var selected = monitors.FirstOrDefault(m => m.Id == _settings.MonitorDeviceName)
            ?? monitors.First(m => m.Id == (Screen.PrimaryScreen ?? screens[0]).DeviceName);
        _settings.MonitorDeviceName = selected.Id;
        _live = new LiveSettings(selected.Bounds, monitors);
    }

    private Task<bool> SelectMonitorAsync(string id)
    {
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (_closing || !IsHandleCreated) return Task.FromResult(false);
        try { BeginInvoke(() =>
        {
            if (_closing) { completion.TrySetResult(false); return; }
            var selected = _live.Monitors.FirstOrDefault(m => m.Id == id);
            if (selected is null) { completion.TrySetResult(false); return; }
            _settings.MonitorDeviceName = id;
            _live = new LiveSettings(selected.Bounds, _live.Monitors);
            _settings.Save();
            completion.TrySetResult(true);
        }); }
        catch (InvalidOperationException) { completion.TrySetResult(false); }
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
        if (_networkCombo.SelectedItem is not NetworkChoice adapter)
        {
            MessageBox.Show(this, "먼저 네트워크 어댑터를 선택하세요.", "PenBridge", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        _startStopButton.Enabled = false;
        SetSetupControlsEnabled(false);

        _injector = new PointerInjector(_log);
        if (!_injector.TryOpen(out string? error))
        {
            _injector = null;
            SetSetupControlsEnabled(true);
            _startStopButton.Enabled = true;
            return;
        }

        PopulateMonitors();
        _server = new PenBridgeServer(_log, _injector, () => _live.Monitor,
            () => MappingMode.PreserveAspectRatio, () => false, () => _live.Monitors, SelectMonitorAsync);
        _server.ConnectionStateChanged += (state, detail) => PostIfOpen(() => OnConnectionStateChanged(state, detail));

        try
        {
            await _server.StartAsync(adapter.Address, (int)_portInput.Value, CancellationToken.None);
            UpdatePairingDisplay();
            _startStopButton.Text = "서버 중지";

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
        }

        _startStopButton.Enabled = true;
    }

    private async Task StopServerAsync()
    {
        _startStopButton.Enabled = false;
        if (_server is not null)
        {
            await _server.DisposeAsync();
            _server = null;
        }
        _injector?.Dispose();
        _injector = null;

        _connectionStatusLabel.Text = "● 중지됨";
        _connectionStatusLabel.ForeColor = Color.Gray;
        _urlBox.Text = string.Empty;
        _qrBox.Image = null;
        _startStopButton.Text = "서버 시작";
        SetSetupControlsEnabled(true);
        _startStopButton.Enabled = true;
    }

    private void SetSetupControlsEnabled(bool enabled)
    {
        _networkCombo.Enabled = enabled;
        _portInput.Enabled = enabled;
    }

    private void UpdatePairingDisplay()
    {
        if (_server is null) return;
        string url = $"http://{_server.BoundAddress}:{_server.BoundPort}";
        _urlBox.Text = url;

        var qrGenerator = new QRCodeGenerator();
        var qrData = qrGenerator.CreateQrCode(url, QRCodeGenerator.ECCLevel.Q);
        var pngQr = new PngByteQRCode(qrData);
        byte[] png = pngQr.GetGraphic(8);
        _qrBox.Image?.Dispose();
        _qrBox.Image = Image.FromStream(new MemoryStream(png));
    }

    private void OnConnectionStateChanged(ConnectionState state, string? detail)
    {
        UpdateTrayState(state, detail);
        (_connectionStatusLabel.Text, _connectionStatusLabel.ForeColor) = state switch
        {
            ConnectionState.Starting => ("● 시작 중...", Color.DarkOrange),
            ConnectionState.WaitingForIPad => ("● iPad 연결 대기 중", Color.DarkOrange),
            ConnectionState.Connected => ($"● 연결됨 ({detail})", Color.SeaGreen),
            ConnectionState.Error => ($"● 오류: {detail}", Color.Firebrick),
            _ => ("● 중지됨", Color.Gray),
        };
    }

    private void AppendLog(LogLevel level, string message)
    {
        string prefix = level switch { LogLevel.Error => "[ERROR]", LogLevel.Warn => "[WARN]", _ => "[INFO]" };
        _log_box.AppendText($"[{DateTime.Now:HH:mm:ss}] {prefix} {message}\r\n");
    }

    private async void OnClosingAsync(object? sender, FormClosingEventArgs e)
    {
        if (_closeReady) return;
        e.Cancel = true;
        if (!_exitRequested && e.CloseReason == CloseReason.UserClosing) { HideToTray(); return; }
        if (_closing) return;
        _closing = true;

        using var progress = new Form
        {
            Text = "PenBridge",
            ClientSize = new Size(340, 125),
            FormBorderStyle = FormBorderStyle.FixedDialog,
            StartPosition = FormStartPosition.CenterParent,
            ControlBox = false,
            ShowInTaskbar = false,
            AutoScaleMode = AutoScaleMode.Dpi,
        };
        progress.Controls.Add(new Label
        {
            Text = "종료 중…\n연결을 정리하고 있습니다. 잠시만 기다려 주세요.",
            AutoSize = false,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleCenter,
        });
        progress.Controls.Add(new ProgressBar
        {
            Dock = DockStyle.Bottom,
            Height = 8,
            Style = ProgressBarStyle.Marquee,
            MarqueeAnimationSpeed = 30,
        });
        progress.Show(this);
        Enabled = false;
        progress.Update();

        try
        {
            // Yield to the message loop so the popup paints before shutdown starts.
            await Task.Yield();
            try { await _serverOperation; }
            catch (Exception ex) { _log.Error($"Server operation failed during shutdown: {ex.Message}"); }
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
