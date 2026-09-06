using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using PenBridge.Input;
using PenBridge.Logging;
using PenBridge.Models;

namespace PenBridge.Networking;

public enum ConnectionState { Stopped, Starting, WaitingForIPad, Connected, Error }

/// <summary>
/// Hosts the iPad-facing HTTP/WebSocket endpoint on Kestrel (not HttpListener/http.sys) so it can
/// bind one explicit local IP:port as a normal, non-elevated user — no URL ACL reservation, no
/// wildcard "+" binding, no admin rights. See the project report for why Kestrel was chosen over
/// HttpListener. Only one iPad session is accepted at a time; a second connection attempt while
/// one is active is rejected outright rather than silently pre-empting the first.
/// </summary>
public sealed class PenBridgeServer : IAsyncDisposable
{
    private const int MaxMessageBytes = 16 * 1024;
    private const int PortProbeAttempts = 10;

    private readonly ILog _log;
    private readonly PointerInjector _injector;
    private readonly Func<MonitorRect> _getMonitor;
    private readonly Func<MappingMode> _getMappingMode;
    private readonly Func<bool> _getAllowNonPenInput;
    private readonly Func<MonitorOption[]> _getMonitors;
    private readonly Func<string, Task<bool>>? _selectMonitor;
    private ClientDetails? _client;
    private long _sampleCount;
    private long _lastInputTicks;
    private int _streaming;

    public ClientDetails? Client
    {
        get
        {
            lock (_sessionGate)
            {
                if (_client is null) return null;
                var ticks = Interlocked.Read(ref _lastInputTicks);
                return _client with { Samples = Interlocked.Read(ref _sampleCount),
                    LastInput = ticks == 0 ? null : new DateTimeOffset(ticks, TimeSpan.Zero),
                    Streaming = Volatile.Read(ref _streaming) != 0 };
            }
        }
    }

    private WebApplication? _app;
    private readonly SemaphoreSlim _videoGate = new(1, 1);
    private readonly object _sessionGate = new();
    private bool _sessionActive;
    private readonly CancellationTokenSource _shutdown = new();

    private DateTime _lastMalformedLogUtc = DateTime.MinValue;
    private int _suppressedMalformedCount;

    public event Action<ConnectionState, string?>? ConnectionStateChanged;

    public IPAddress BoundAddress { get; private set; } = IPAddress.None;
    public int BoundPort { get; private set; }

    public PenBridgeServer(
        ILog log,
        PointerInjector injector,
        Func<MonitorRect> getMonitor,
        Func<MappingMode> getMappingMode,
        Func<bool> getAllowNonPenInput,
        Func<MonitorOption[]>? getMonitors = null,
        Func<string, Task<bool>>? selectMonitor = null)
    {
        _log = log;
        _injector = injector;
        _getMonitor = getMonitor;
        _getMappingMode = getMappingMode;
        _getAllowNonPenInput = getAllowNonPenInput;
        _getMonitors = getMonitors ?? (() => []);
        _selectMonitor = selectMonitor;
    }

    /// <summary>Tries the requested port, then a handful of following ports if it's in use.
    /// Throws only if none of them work, with a message the UI can show directly.</summary>
    public async Task StartAsync(IPAddress address, int preferredPort, CancellationToken token)
    {
        ConnectionStateChanged?.Invoke(ConnectionState.Starting, null);

        Exception? lastError = null;
        for (int attempt = 0; attempt < PortProbeAttempts; attempt++)
        {
            int port = preferredPort + attempt;
            try
            {
                await StartOnPortAsync(address, port, token);
                BoundAddress = address;
                BoundPort = port;
                _log.Info($"Server listening on http://{address}:{port}/");
                ConnectionStateChanged?.Invoke(ConnectionState.WaitingForIPad, null);
                return;
            }
            catch (IOException ex)
            {
                lastError = ex;
                _log.Warn($"Port {port} unavailable ({ex.Message}), trying next port.");
                if (_app is not null)
                {
                    await _app.DisposeAsync();
                    _app = null;
                }
            }
        }

        string message = $"포트 {preferredPort}부터 {PortProbeAttempts}개를 시도했지만 모두 사용 중입니다: {lastError?.Message}";
        ConnectionStateChanged?.Invoke(ConnectionState.Error, message);
        throw new IOException(message, lastError);
    }

    private async Task StartOnPortAsync(IPAddress address, int port, CancellationToken token)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders(); // we log through ILog ourselves; ASP.NET's own console logger has nowhere to write in a WinExe anyway
        builder.WebHost.UseUrls($"http://{address}:{port}/");

        var app = builder.Build();
        app.UseWebSockets();
        app.Use(async (ctx, next) =>
        {
            ctx.Response.Headers.CacheControl = "no-store";
            ctx.Response.Headers["X-Content-Type-Options"] = "nosniff";
            await next();
        });

        string expectedOrigin = $"http://{address}:{port}";

        app.MapGet("/health", () => Results.Json(new { service = "PenBridge", deviceName = Environment.MachineName, version = 2 }));

        // Refresh monitor inventory after reconnects, remote selection, and display changes.
        app.MapGet("/config", (HttpContext ctx) =>
        {
            return Results.Text(BuildConfigJson(), "application/json");
        });

        app.MapPost("/monitor", async (HttpContext ctx) =>
        {
            if (!string.Equals(ctx.Request.Headers.Origin, expectedOrigin, StringComparison.OrdinalIgnoreCase))
                return Results.StatusCode(403);
            // The id is selected from the server-owned inventory, never used as a path or command.
            string id = ctx.Request.Query["id"].ToString();
            if (id.Length > 128 || !_getMonitors().Any(m => m.Id == id)) return Results.BadRequest();
            if (_selectMonitor is null || !await _selectMonitor(id)) return Results.Conflict();
            return Results.Text(BuildConfigJson(), "application/json");
        });

        app.MapGet("/stream.mp4", async (HttpContext ctx) =>
        {
            if (!File.Exists(VideoStreamer.Executable))
            {
                _log.Error("영상 인코더를 찾을 수 없습니다. 실행 파일 옆 tools 폴더를 확인하세요.");
                ctx.Response.StatusCode = 503; return;
            }
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ctx.RequestAborted, _shutdown.Token);
            var ct = linked.Token;
            if (!await _videoGate.WaitAsync(TimeSpan.FromSeconds(5), ct)) { ctx.Response.StatusCode = 409; return; }
            try
            {
                Interlocked.Exchange(ref _streaming, 1);
                int ReadOption(string name, int fallback) => int.TryParse(ctx.Request.Query[name], out var value) ? value : fallback;
                ctx.Response.ContentType = "video/mp4";
                ctx.Response.Headers["X-Accel-Buffering"] = "no";
                await VideoStreamer.StreamAsync(ctx.Response.Body, _getMonitor(), ReadOption("fps", 30),
                    ReadOption("width", 1280), ReadOption("crf", 23), ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) {}
            catch (IOException) when (ct.IsCancellationRequested) {}
            catch (Exception ex)
            {
                _log.Error($"영상 스트리밍 오류: {ex.Message}");
                if (!ctx.Response.HasStarted) ctx.Response.StatusCode = 500;
                else ctx.Abort();
            }
            finally { Interlocked.Exchange(ref _streaming, 0); _videoGate.Release(); }
        });

        app.MapGet("/", () => Results.Json(new
        {
            service = "PenBridge", deviceName = Environment.MachineName,
            message = "공용 PenBridge 웹사이트에서 이 IP 주소를 입력하거나 QR 코드를 스캔하세요."
        }));

        app.MapGet("/connect", (HttpContext ctx) =>
        {
            string assets = ctx.Request.Query["assets"].ToString();
            if (!Uri.TryCreate(assets, UriKind.Absolute, out var baseUri) ||
                (baseUri.Scheme != Uri.UriSchemeHttps && !(baseUri.Scheme == Uri.UriSchemeHttp && baseUri.IsLoopback)))
                return Results.BadRequest("올바른 공용 웹사이트 주소가 필요합니다.");
            var loader = new Uri(baseUri, "pad-loader.js").AbsoluteUri;
            string loaderJson = JsonSerializer.Serialize(loader);
            string html = "<!doctype html><html lang=\"ko\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width,initial-scale=1,viewport-fit=cover\"><title>PenBridge</title></head><body style=\"margin:0;background:#111;color:#fff;font-family:system-ui;display:grid;place-items:center;min-height:100vh\">연결 준비 중…<script>fetch('/config').then(r=>r.json()).then(c=>{window.__PENBRIDGE_CONFIG__=c;const s=document.createElement('script');s.src=" + loaderJson + ";s.onerror=()=>document.body.textContent='공용 웹사이트 파일을 불러오지 못했습니다.';document.head.append(s)}).catch(()=>document.body.textContent='PC 설정을 불러오지 못했습니다.');</script></body></html>";
            return Results.Content(html, "text/html; charset=utf-8");
        });

        app.Map("/ws", async (HttpContext ctx) =>
        {
            if (!ctx.WebSockets.IsWebSocketRequest)
            {
                ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
                return;
            }
            string? origin = ctx.Request.Headers.Origin.FirstOrDefault();
            if (!string.Equals(origin, expectedOrigin, StringComparison.OrdinalIgnoreCase))
            {
                _log.Warn($"Rejected WebSocket upgrade with unexpected Origin '{origin}' (expected '{expectedOrigin}').");
                ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
                return;
            }

            lock (_sessionGate)
            {
                if (_sessionActive)
                {
                    ctx.Response.StatusCode = StatusCodes.Status409Conflict;
                    return;
                }
                _sessionActive = true;
            }

            string remoteIp = ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown";
            var session = new PointerSessionState();
            try
            {
                using var socket = await ctx.WebSockets.AcceptWebSocketAsync();
                lock (_sessionGate)
                {
                    _sampleCount = 0; _lastInputTicks = 0;
                    string agent = ctx.Request.Headers.UserAgent.ToString();
                    _client = new ClientDetails($"{remoteIp}:{ctx.Connection.RemotePort}",
                        agent[..Math.Min(agent.Length, 512)], DateTimeOffset.UtcNow, 0, null, false);
                }
                _log.Info($"iPad connected from {remoteIp}");
                ConnectionStateChanged?.Invoke(ConnectionState.Connected, remoteIp);
                await RunSessionAsync(socket, session, token, ctx.RequestAborted);
            }
            catch (OperationCanceledException) when (_shutdown.IsCancellationRequested || ctx.RequestAborted.IsCancellationRequested) {}
            catch (Exception ex) { _log.Error($"클라이언트 연결 오류: {ex.Message}"); }
            finally
            {
                ReleaseIfStillDown(session);
                lock (_sessionGate) { _sessionActive = false; _client = null; }
                _log.Info($"iPad disconnected ({remoteIp})");
                ConnectionStateChanged?.Invoke(ConnectionState.WaitingForIPad, null);
            }
        });

        app.MapFallback(() => Results.NotFound());

        await app.StartAsync(token);
        _app = app;
    }

    private static double AspectRatioOf(MonitorRect monitor) =>
        monitor.Height <= 0 ? 16.0 / 9.0 : (double)monitor.Width / monitor.Height;

    private static string MappingKey(MonitorRect monitor, MappingMode mode) =>
        $"{monitor.Left},{monitor.Top},{monitor.Width},{monitor.Height},{(int)mode}";

    private string BuildConfigJson()
    {
        var monitor = _getMonitor();
        var mode = _getMappingMode();
        return JsonSerializer.Serialize(new
        {
            mappingKey = MappingKey(monitor, mode),
            mappingMode = mode == MappingMode.PreserveAspectRatio ? "preserveAspectRatio" : "stretch",
            targetAspect = AspectRatioOf(monitor),
            allowNonPen = _getAllowNonPenInput(),
            monitors = _getMonitors().Select(m => new { id = m.Id, label = m.Label }),
            selectedMonitor = _getMonitors().FirstOrDefault(m => m.Bounds == monitor)?.Id,
        });
    }

    private async Task RunSessionAsync(WebSocket socket, PointerSessionState session, CancellationToken serverToken, CancellationToken requestToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(serverToken, requestToken, _shutdown.Token);
        var token = linked.Token;
        var lastMonitor = _getMonitor();

        try
        {
            while (socket.State == WebSocketState.Open && !token.IsCancellationRequested)
            {
                byte[]? message;
                try
                {
                    message = await ReceiveFullMessageAsync(socket, token);
                }
                catch (WebSocketException) { break; }
                catch (OperationCanceledException) { break; }

                if (message is null)
                    break; // client sent Close, or the message exceeded the size cap and the socket was already closed

                string json = Encoding.UTF8.GetString(message);
                if (!TryParseSample(json, out var raw, out string? receivedMappingKey, out string? parseError))
                {
                    LogMalformedThrottled(parseError ?? "unknown parse error");
                    continue;
                }
                if (!PointerMapper.TryValidate(raw, out var sanitized))
                {
                    LogMalformedThrottled("non-finite coordinate/pressure value");
                    continue;
                }

                var monitor = _getMonitor();
                if (receivedMappingKey != MappingKey(monitor, _getMappingMode()))
                {
                    // Release on the OLD monitor before accepting a different coordinate space.
                    foreach (var release in session.BuildForcedReleases())
                        _injector.Inject(release, lastMonitor);
                    await socket.SendAsync(Encoding.UTF8.GetBytes(BuildConfigJson()),
                        WebSocketMessageType.Text, true, token);
                    continue;
                }
                if (monitor != lastMonitor)
                    foreach (var oldRelease in session.BuildForcedReleases())
                        _injector.Inject(oldRelease, lastMonitor);
                lastMonitor = monitor;

                var toInject = session.Process(sanitized);
                if (toInject is { } sample)
                {
                    Interlocked.Increment(ref _sampleCount);
                    Interlocked.Exchange(ref _lastInputTicks, DateTimeOffset.UtcNow.Ticks);
                    if (!_injector.Inject(sample, monitor))
                    {
                        _log.Error("펜 입력을 전달하지 못해 장치를 다시 연결합니다.");
                        // Discard the failed native sequence and reconnect with a fresh device.
                        _injector.Dispose();
                        if (!_injector.TryOpen(out var error))
                            _log.Error($"Failed to recover pointer device: {error}");
                        session.BuildForcedReleases();
                        break;
                    }
                }
            }
        }
        finally
        {
            foreach (var release in session.BuildForcedReleases())
                _injector.Inject(release, lastMonitor);
            if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                try { await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None); }
                catch (WebSocketException) { /* client already gone */ }
            }
        }
    }

    /// <summary>If the connection drops mid-stroke, forces a synthetic Up at the last known
    /// position instead of leaving Windows believing the pen is still pressed.</summary>
    private void ReleaseIfStillDown(PointerSessionState session)
    {
        foreach (var release in session.BuildForcedReleases())
        {
            _log.Warn("Connection ended while an input was down — forcing a release.");
            _injector.Inject(release, _getMonitor());
        }
    }

    /// <summary>Accumulates WebSocket frames until EndOfMessage, enforcing a size cap and
    /// rejecting binary frames — a single ReceiveAsync call is not guaranteed to return a
    /// complete message.</summary>
    private static async Task<byte[]?> ReceiveFullMessageAsync(WebSocket socket, CancellationToken token)
    {
        using var stream = new MemoryStream();
        var buffer = new byte[4096];
        WebSocketReceiveResult result;
        do
        {
            result = await socket.ReceiveAsync(buffer, token);
            if (result.MessageType == WebSocketMessageType.Close)
                return null;
            if (result.MessageType == WebSocketMessageType.Binary)
            {
                await socket.CloseAsync(WebSocketCloseStatus.InvalidMessageType, "binary frames are not supported", token);
                return null;
            }
            if (stream.Length + result.Count > MaxMessageBytes)
            {
                await socket.CloseAsync(WebSocketCloseStatus.MessageTooBig, $"message exceeded {MaxMessageBytes} bytes", token);
                return null;
            }
            stream.Write(buffer, 0, result.Count);
        } while (!result.EndOfMessage);
        return stream.ToArray();
    }

    internal static bool TryParseSample(string json, out PenSample sample, out string? mappingKey, out string? error)
    {
        sample = default;
        mappingKey = null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.TryGetProperty("mappingKey", out var keyEl) && keyEl.ValueKind == JsonValueKind.String)
                mappingKey = keyEl.GetString();

            if (!root.TryGetProperty("v", out var vEl) || vEl.GetInt32() != PenSample.ProtocolVersion)
            {
                error = $"unsupported or missing protocol version (expected {PenSample.ProtocolVersion})";
                return false;
            }
            if (!TryParsePhase(root.GetProperty("phase").GetString(), out var phase))
            {
                error = "unknown phase";
                return false;
            }

            double x = root.GetProperty("x").GetDouble();
            double y = root.GetProperty("y").GetDouble();
            double pressure = root.GetProperty("pressure").GetDouble();
            double tiltXRaw = root.TryGetProperty("tiltX", out var tx) ? tx.GetDouble() : 0;
            double tiltYRaw = root.TryGetProperty("tiltY", out var ty) ? ty.GetDouble() : 0;
            bool inContact = root.TryGetProperty("inContact", out var ic) && ic.GetBoolean();

            if (!double.IsFinite(tiltXRaw) || !double.IsFinite(tiltYRaw))
            {
                error = "non-finite tilt value";
                return false;
            }

            int? rotation = null;
            if (root.TryGetProperty("rotation", out var rotationElement) && rotationElement.ValueKind != JsonValueKind.Null)
            {
                double value = rotationElement.GetDouble();
                if (!double.IsFinite(value)) { error = "non-finite rotation"; return false; }
                rotation = (int)Math.Round(((value % 360) + 360) % 360) % 360;
            }
            bool eraser = root.TryGetProperty("eraser", out var eraserElement) && eraserElement.GetBoolean();
            bool barrel = root.TryGetProperty("barrel", out var barrelElement) && barrelElement.GetBoolean();
            int pointerId = root.TryGetProperty("pointerId", out var pointerElement) ? pointerElement.GetInt32() : 1;
            bool isTouch = root.TryGetProperty("pointerType", out var typeElement) && typeElement.GetString() == "touch";
            if (pointerId < 0) { error = "invalid pointer id"; return false; }
            sample = new PenSample(phase, inContact, x, y, pressure,
                (int)Math.Round(Math.Clamp(tiltXRaw, -90, 90)), (int)Math.Round(Math.Clamp(tiltYRaw, -90, 90)), rotation, eraser, barrel, pointerId, isTouch);
            error = null;
            return true;
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or FormatException or OverflowException)
        {
            error = ex.Message;
            return false;
        }
    }

    private static bool TryParsePhase(string? s, out PenPhase phase)
    {
        switch (s)
        {
            case "down": phase = PenPhase.Down; return true;
            case "move": phase = PenPhase.Move; return true;
            case "up": phase = PenPhase.Up; return true;
            default: phase = default; return false;
        }
    }

    private void LogMalformedThrottled(string reason)
    {
        var now = DateTime.UtcNow;
        if (now - _lastMalformedLogUtc < TimeSpan.FromSeconds(1))
        {
            _suppressedMalformedCount++;
            return;
        }
        if (_suppressedMalformedCount > 0)
            _log.Warn($"...and {_suppressedMalformedCount} more malformed messages suppressed in the last second.");
        _log.Warn($"Dropped malformed pen message: {reason}");
        _lastMalformedLogUtc = now;
        _suppressedMalformedCount = 0;
    }

    public async ValueTask DisposeAsync()
    {
        _shutdown.Cancel();
        if (_app is not null)
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
            _app = null;
        }
        ConnectionStateChanged?.Invoke(ConnectionState.Stopped, null);
    }
}
