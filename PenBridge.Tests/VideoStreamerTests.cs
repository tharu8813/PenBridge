using PenBridge.Input;
using PenBridge.Networking;

namespace PenBridge.Tests;

public class VideoStreamerTests
{
    [Fact]
    public void Encoder_uses_physical_monitor_offsets_and_bounded_settings()
    {
        var info = VideoStreamer.BuildStartInfo(new MonitorRect(-1920, -1080, 1920, 1080), 999, 99999, -4);
        var args = info.ArgumentList.ToList();
        string After(string option) => args[args.IndexOf(option) + 1];
        Assert.Equal("-1920", After("-offset_x"));
        Assert.Equal("-1080", After("-offset_y"));
        Assert.Equal("60", After("-framerate"));
        Assert.Equal("18", After("-crf"));
        Assert.Contains("1920", After("-vf"));
        Assert.Equal("0", After("-bf"));
        Assert.Equal("mp4", After("-f") == "gdigrab" ? args[args.LastIndexOf("-f") + 1] : "");
        Assert.Contains("frag_every_frame", After("-movflags"));
        Assert.DoesNotContain("frag_keyframe", After("-movflags"));
        Assert.True(info.CreateNoWindow);
        Assert.False(info.UseShellExecute);
    }
}
