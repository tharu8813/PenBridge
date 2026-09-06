using PenBridge.Models;
using PenBridge.Networking;

namespace PenBridge.Tests;

public class MessageParsingTests
{
    private const string Valid = """{"v":1,"phase":"down","inContact":true,"x":0.5,"y":0.5,"pressure":0.8,"tiltX":10,"tiltY":-5}""";

    [Fact]
    public void Valid_message_parses()
    {
        Assert.True(PenBridgeServer.TryParseSample(Valid, out var sample, out _, out _));
        Assert.Equal(PenPhase.Down, sample.Phase);
        Assert.True(sample.InContact);
        Assert.Equal(0.5, sample.X);
        Assert.Equal(10, sample.TiltX);
    }

    [Fact]
    public void Missing_protocol_version_is_rejected()
    {
        const string json = """{"phase":"down","inContact":true,"x":0.5,"y":0.5,"pressure":0.8}""";
        Assert.False(PenBridgeServer.TryParseSample(json, out _, out _, out string? error));
        Assert.NotNull(error);
    }

    [Fact]
    public void Mismatched_protocol_version_is_rejected()
    {
        const string json = """{"v":999,"phase":"down","inContact":true,"x":0.5,"y":0.5,"pressure":0.8}""";
        Assert.False(PenBridgeServer.TryParseSample(json, out _, out _, out _));
    }

    [Fact]
    public void Unknown_phase_is_rejected()
    {
        const string json = """{"v":1,"phase":"sideways","inContact":true,"x":0.5,"y":0.5,"pressure":0.8}""";
        Assert.False(PenBridgeServer.TryParseSample(json, out _, out _, out _));
    }

    [Fact]
    public void Missing_required_field_is_rejected()
    {
        const string json = """{"v":1,"phase":"down","inContact":true,"y":0.5,"pressure":0.8}"""; // no "x"
        Assert.False(PenBridgeServer.TryParseSample(json, out _, out _, out _));
    }

    [Fact]
    public void Wrong_type_for_numeric_field_is_rejected()
    {
        const string json = """{"v":1,"phase":"down","inContact":true,"x":"not-a-number","y":0.5,"pressure":0.8}""";
        Assert.False(PenBridgeServer.TryParseSample(json, out _, out _, out _));
    }

    [Fact]
    public void Malformed_json_is_rejected()
    {
        Assert.False(PenBridgeServer.TryParseSample("{not json at all", out _, out _, out _));
    }

    [Fact]
    public void Non_finite_tilt_is_rejected()
    {
        // 1e400 overflows a double to +Infinity — System.Text.Json accepts the literal, GetDouble() returns Infinity.
        const string json = """{"v":1,"phase":"move","inContact":false,"x":0.5,"y":0.5,"pressure":0.5,"tiltX":1e400,"tiltY":0}""";
        Assert.False(PenBridgeServer.TryParseSample(json, out _, out _, out _));
    }

    [Fact]
    public void Missing_optional_tilt_defaults_to_zero()
    {
        const string json = """{"v":1,"phase":"move","inContact":false,"x":0.5,"y":0.5,"pressure":0.5}""";
        Assert.True(PenBridgeServer.TryParseSample(json, out var sample, out _, out _));
        Assert.Equal(0, sample.TiltX);
        Assert.Equal(0, sample.TiltY);
    }
}
