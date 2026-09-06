using PenBridge.Networking;
using Forms = System.Windows.Forms;

namespace PenBridge.UI;

public partial class MainWindow
{
    private readonly Forms.NotifyIcon _tray = new();
    private readonly Forms.ContextMenuStrip _trayMenu = new();
    private bool _exitRequested;
    private ConnectionState _trayState = ConnectionState.Stopped;
    private string _lastError = "없음";
    private DateTime _lastErrorNotice = DateTime.MinValue;
    private ClientDetailsWindow? _detailsWindow;

    /// <summary>Builds the tray icon and its context menu. Kept deliberately short: a status line,
    /// the handful of real actions, and a separator before Exit — not a dump of live connection
    /// stats (those live in the "연결 상세 정보" window, which refreshes every second on its own).</summary>
    private void InitializeTray()
    {
        _tray.Icon = System.Drawing.Icon.ExtractAssociatedIcon(
            Environment.ProcessPath ?? System.Diagnostics.Process.GetCurrentProcess().MainModule!.FileName!);
        _tray.Text = "PenBridge · 중지됨";
        _tray.ContextMenuStrip = _trayMenu;
        _tray.Visible = true;
        _tray.DoubleClick += (_, _) => RestoreWindow();
        _tray.BalloonTipClicked += (_, _) => ShowClientDetails();
        _trayMenu.Opening += (_, _) => BuildTrayMenu();
    }

    private void HideToTray()
    {
        Hide();
        ShowInTaskbar = false;
    }

    private void RestoreWindow()
    {
        if (_closing) return;
        ShowInTaskbar = true;
        Show();
        WindowState = System.Windows.WindowState.Normal;
        Activate();
    }

    private async Task RunServerOperationAsync()
    {
        if (_closing || !_serverOperation.IsCompleted) return;
        _serverOperation = ToggleServerAsync();
        try { await _serverOperation; }
        catch (Exception ex) { _log.Error($"서버 처리 오류: {ex.Message}"); }
        finally { if (!_closing) StartStopButton.IsEnabled = true; }
    }

    private void UpdateTrayState(ConnectionState state, string? detail)
    {
        var previous = _trayState;
        _trayState = state;
        _tray.Text = $"PenBridge · {StateLabel(state)}";
        if (state == ConnectionState.Connected && previous != state)
            _tray.ShowBalloonTip(4000, "iPad 연결됨", $"{detail}\n펜 입력을 사용할 수 있습니다.", Forms.ToolTipIcon.Info);
        else if (previous == ConnectionState.Connected && state != ConnectionState.Connected)
            _tray.ShowBalloonTip(4000, "iPad 연결 해제", "클라이언트 연결이 종료되었습니다.", Forms.ToolTipIcon.Info);
        if (state == ConnectionState.Error) NotifyError(detail ?? "서버 오류가 발생했습니다.");
    }

    private void NotifyError(string message)
    {
        _lastError = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}\r\n{message}";
        if (DateTime.Now - _lastErrorNotice < TimeSpan.FromSeconds(10)) return;
        _lastErrorNotice = DateTime.Now;
        _tray.ShowBalloonTip(5000, "PenBridge 오류", message[..Math.Min(message.Length, 240)], Forms.ToolTipIcon.Error);
    }

    private static string StateLabel(ConnectionState state) => state switch
    {
        ConnectionState.Connected => "클라이언트 연결됨",
        ConnectionState.WaitingForIPad => "연결 대기 중",
        ConnectionState.Starting => "서버 시작 중",
        ConnectionState.Error => "오류",
        _ => "중지됨",
    };

    private void BuildTrayMenu()
    {
        while (_trayMenu.Items.Count > 0) _trayMenu.Items[0].Dispose();
        _trayMenu.Items.Add(new Forms.ToolStripMenuItem($"PenBridge · {StateLabel(_trayState)}") { Enabled = false });
        _trayMenu.Items.Add(new Forms.ToolStripSeparator());
        _trayMenu.Items.Add("창 열기", null, (_, _) => RestoreWindow());
        var toggle = _trayMenu.Items.Add(_server is null ? "서버 시작" : "서버 중지", null,
            async (_, _) => await RunServerOperationAsync());
        toggle.Enabled = _serverOperation.IsCompleted && !_closing;
        _trayMenu.Items.Add("연결 상세 정보…", null, (_, _) => ShowClientDetails());
        _trayMenu.Items.Add(new Forms.ToolStripSeparator());
        _trayMenu.Items.Add("PenBridge 종료", null, (_, _) =>
        {
            _exitRequested = true;
            RestoreWindow();
            _detailsWindow?.Close();
            Close();
        });
    }

    private string BuildDetailsText()
    {
        var client = _server?.Client;
        var monitor = _live.Monitors.FirstOrDefault(m => m.Bounds == _live.Monitor);
        string summary = $"상태: {StateLabel(_trayState)}\r\n" +
            $"서버: {(_server is null ? "중지됨" : $"{_server.BoundAddress}:{_server.BoundPort}")}\r\n" +
            $"대상 모니터: {monitor?.Label ?? "없음"}\r\n\r\n";
        if (client is null) summary += "연결된 클라이언트가 없습니다.\r\n";
        else summary += $"클라이언트 주소: {client.Address}\r\n" +
            $"연결 시각: {client.ConnectedAt.LocalDateTime:yyyy-MM-dd HH:mm:ss}\r\n" +
            $"연결 시간: {ConnectedDuration(client.ConnectedAt)}\r\n" +
            $"처리한 입력: {client.Samples:N0}개\r\n" +
            $"마지막 입력: {client.LastInput?.LocalDateTime.ToString("HH:mm:ss") ?? "없음"}\r\n" +
            $"영상: {(client.Streaming ? "스트리밍 중" : "전송 꺼짐")}\r\n" +
            $"브라우저 제공 정보:\r\n{client.UserAgent}\r\n";
        return summary + $"\r\n최근 오류:\r\n{_lastError}\r\n\r\n화면·입력 설정은 iPad의 설정 버튼에서 변경합니다.";
    }

    private void ShowClientDetails()
    {
        if (_closing) return;
        if (_detailsWindow is not null) { _detailsWindow.Show(); _detailsWindow.Activate(); return; }
        var window = new ClientDetailsWindow(BuildDetailsText, Icon) { Owner = IsVisible ? this : null };
        window.Closed += (_, _) => _detailsWindow = null;
        _detailsWindow = window;
        window.Show();
    }

    private static string ConnectedDuration(DateTimeOffset start)
    {
        var duration = DateTimeOffset.UtcNow - start;
        return $"{(int)duration.TotalHours:00}:{duration.Minutes:00}:{duration.Seconds:00}";
    }
}
