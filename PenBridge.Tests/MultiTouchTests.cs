using PenBridge.Input;
using PenBridge.Models;
using PenBridge.Networking;

namespace PenBridge.Tests;

public class MultiTouchTests
{
    [Fact]
    public void Touch_info_keeps_native_id_position_and_contact_flags()
    {
        var sample = Touch(PenPhase.Down, 17, .25, .75);
        var info = PointerMapper.BuildTouchInfo(sample, new(1920, 0, 1920, 1080), new(0, 0, 3840, 1080), 2, 9, false);
        Assert.Equal(NativeMethods.PT_TOUCH, info.type);
        Assert.Equal(2u, info.touchInfo.pointerInfo.pointerId);
        Assert.Equal(2400, info.touchInfo.pointerInfo.ptPixelLocation.x);
        Assert.Equal(0u, info.touchInfo.pointerInfo.pointerFlags & NativeMethods.POINTER_FLAG_PRIMARY);
        Assert.NotEqual(0u, info.touchInfo.pointerInfo.pointerFlags & NativeMethods.POINTER_FLAG_INCONTACT);
        Assert.Equal(512u, info.touchInfo.pressure);
    }

    [Fact]
    public void Independent_fingers_release_without_affecting_each_other()
    {
        var state = new PointerSessionState();
        state.Process(Touch(PenPhase.Down, 11));
        state.Process(Touch(PenPhase.Down, 22));
        Assert.Equal(PenPhase.Up, state.Process(Touch(PenPhase.Up, 11))!.Value.Phase);
        var releases = state.BuildForcedReleases();
        Assert.Single(releases);
        Assert.Equal(22, releases[0].PointerId);
    }

    [Fact]
    public void Every_native_touch_frame_contains_all_active_contacts()
    {
        var first = Touch(PenPhase.Move, 11, .2, .3);
        var active = new Dictionary<int, PenSample> { [11] = first };
        var ids = new Dictionary<int, uint> { [11] = 1, [22] = 2 };
        var frame = PointerInjector.ComposeTouchFrame(Touch(PenPhase.Down, 22, .8, .7), 2, active, ids);
        Assert.Equal(2, frame.Length);
        Assert.Equal((11, PenPhase.Move, 1u), (frame[0].Sample.PointerId, frame[0].Sample.Phase, frame[0].NativeId));
        Assert.Equal((22, PenPhase.Down, 2u), (frame[1].Sample.PointerId, frame[1].Sample.Phase, frame[1].NativeId));
    }

    [Fact]
    public void Wire_parser_reads_touch_identity()
    {
        Assert.True(PenBridgeServer.TryParseSample("""{"v":1,"phase":"down","inContact":true,"x":0.2,"y":0.3,"pressure":0.5,"pointerId":42,"pointerType":"touch"}""", out var sample, out _));
        Assert.True(sample.IsTouch);
        Assert.Equal(42, sample.PointerId);
    }

    private static PenSample Touch(PenPhase phase, int id, double x=.5, double y=.5) =>
        new(phase, phase != PenPhase.Up, x, y, phase == PenPhase.Up ? 0 : .5, 0, 0, PointerId:id, IsTouch:true);
}
