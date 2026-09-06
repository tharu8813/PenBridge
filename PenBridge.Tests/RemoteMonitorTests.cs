using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text.Json;
using PenBridge.Input;
using PenBridge.Logging;
using PenBridge.Models;
using PenBridge.Networking;

namespace PenBridge.Tests;

public class RemoteMonitorTests
{
    [Fact]
    public async Task Remote_monitor_requires_local_origin_and_updates_config_without_password()
    {
        var monitors = new[] {
            new MonitorOption("primary", "주 모니터", new MonitorRect(0, 0, 1920, 1080)),
            new MonitorOption("left", "보조 모니터", new MonitorRect(-1280, 0, 1280, 1024)) };
        var selected = monitors[0];
        var log = new TestLog();
        using var injector = new PointerInjector(log); // Deliberately unopened: no desktop input in this test.
        await using var server = new PenBridgeServer(log, injector, () => selected.Bounds,
            () => MappingMode.PreserveAspectRatio, () => false, () => monitors,
            id => { selected = monitors.Single(m => m.Id == id); return Task.FromResult(true); });
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start(); int port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop();
        await server.StartAsync(IPAddress.Loopback, port, CancellationToken.None);
        string origin = $"http://127.0.0.1:{server.BoundPort}";
        using var http = new HttpClient { BaseAddress = new Uri(origin) };
        using var health = JsonDocument.Parse(await http.GetStringAsync("/health"));
        Assert.Equal("PenBridge", health.RootElement.GetProperty("service").GetString());
        using var unsafeAssets = await http.GetAsync("/connect?assets=http%3A%2F%2Fexample.com%2F");
        Assert.Equal(HttpStatusCode.BadRequest, unsafeAssets.StatusCode);
        string bootstrap = await http.GetStringAsync("/connect?assets=https%3A%2F%2Fpages.example%2Fpenbridge%2F");
        Assert.Contains("https://pages.example/penbridge/pad-loader.js", bootstrap);
        Assert.DoesNotContain("Apple Pencil로 이 화면에 필기하세요", bootstrap);
        async Task<HttpResponseMessage> Select(string id, string? requestOrigin)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, $"/monitor?id={id}");
            if (requestOrigin is not null) request.Headers.Add("Origin", requestOrigin);
            return await http.SendAsync(request);
        }
        using var wrongOrigin = await Select("left", "http://wrong.test");
        Assert.Equal(HttpStatusCode.Forbidden, wrongOrigin.StatusCode);
        using var unknown = await Select("missing", origin);
        Assert.Equal(HttpStatusCode.BadRequest, unknown.StatusCode);
        Assert.Equal("primary", selected.Id);
        using var valid = await Select("left", origin);
        Assert.Equal(HttpStatusCode.OK, valid.StatusCode);
        using var config = JsonDocument.Parse(await valid.Content.ReadAsStringAsync());
        Assert.Equal("left", config.RootElement.GetProperty("selectedMonitor").GetString());
        Assert.Equal(2, config.RootElement.GetProperty("monitors").GetArrayLength());
        Assert.Equal(1.25, config.RootElement.GetProperty("targetAspect").GetDouble());

        using var socket = new ClientWebSocket();
        socket.Options.SetRequestHeader("Origin", origin);
        socket.Options.SetRequestHeader("User-Agent", "PenBridge integration client");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await socket.ConnectAsync(new Uri($"ws://127.0.0.1:{server.BoundPort}/ws"), timeout.Token);
        while (server.Client is null) await Task.Delay(10, timeout.Token);
        var client = server.Client!;
        Assert.StartsWith("127.0.0.1:", client.Address);
        Assert.Equal("PenBridge integration client", client.UserAgent);
        Assert.Equal(0, client.Samples);
        Assert.Null(client.LastInput);
        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, null, timeout.Token);
        while (server.Client is not null) await Task.Delay(10, timeout.Token);
    }

    private sealed class TestLog : ILog
    {
        public event Action<LogLevel, string>? Logged { add {} remove {} }
        public void Info(string message) {}
        public void Warn(string message) {}
        public void Error(string message) {}
    }
}
