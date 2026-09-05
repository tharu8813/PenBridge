using PenBridge.Input;
using PenBridge.Models;

namespace PenBridge.Tests;

public class PointerMapperTests
{
    [Theory]
    // Primary target with a monitor to the left / above / both.
    [InlineData(0, 0, 2560, 1440, -1920, 0, 1, 0, 4479, 0)]
    [InlineData(0, 0, 2560, 1440, 0, -1080, 0, 0, 0, 1080)]
    [InlineData(0, 0, 2560, 1440, -1920, -1080, 1, 1, 4479, 2519)]
    // The leftmost target starts at injection x=0, not x=-1920.
    [InlineData(-1920, 0, 1920, 1080, -1920, 0, 0, 0, 0, 0)]
    [InlineData(-1920, 0, 1920, 1080, -1920, 0, 1, 1, 1919, 1079)]
    // Single monitor and monitors to the right retain their existing coordinates.
    [InlineData(0, 0, 1920, 1080, 0, 0, 1, 1, 1919, 1079)]
    [InlineData(1920, 0, 2560, 1440, 0, 0, 0, 0, 1920, 0)]
    public void Native_pen_info_uses_virtual_top_left_origin(int left, int top, int width,
        int height, int virtualLeft, int virtualTop, double x, double y, int expectedX, int expectedY)
    {
        var monitor = new MonitorRect(left, top, width, height);
        var desktop = new MonitorRect(virtualLeft, virtualTop, 6400, 3600);
        var sample = new PenSample(PenPhase.Down, true, x, y, 0.5, 0, 0);
        var info = PointerMapper.BuildPenInfo(sample, monitor, desktop, 1, 1);
        Assert.Equal(expectedX, info.penInfo.pointerInfo.ptPixelLocation.x);
        Assert.Equal(expectedY, info.penInfo.pointerInfo.ptPixelLocation.y);
        // Screen capture must keep using primary-relative coordinates.
        var screen = PointerMapper.ToScreenPoint(x, y, monitor);
        Assert.Equal(screen.x, expectedX + virtualLeft);
        Assert.Equal(screen.y, expectedY + virtualTop);
    }

    private static readonly MonitorRect Primary1080p = new(Left: 0, Top: 0, Width: 1920, Height: 1080);

    // --- Coordinate mapping ---

    [Theory]
    [InlineData(0.0, 0.0, 0, 0)]
    [InlineData(1.0, 1.0, 1919, 1079)] // off-by-one: normalized 1.0 must land on the LAST pixel, not one past it
    [InlineData(0.5, 0.5, 960, 540)]
    public void ToScreenPoint_maps_normalized_coordinates_on_primary_monitor(double nx, double ny, int expectedX, int expectedY)
    {
        var p = PointerMapper.ToScreenPoint(nx, ny, Primary1080p);
        Assert.Equal(expectedX, p.x);
        Assert.Equal(expectedY, p.y);
    }

    [Fact]
    public void ToScreenPoint_handles_negative_virtual_screen_origin()
    {
        // A monitor positioned to the left of the primary monitor has a negative Left in
        // virtual-desktop coordinates.
        var leftMonitor = new MonitorRect(Left: -1920, Top: 0, Width: 1920, Height: 1080);
        var p = PointerMapper.ToScreenPoint(0.0, 0.0, leftMonitor);
        Assert.Equal(-1920, p.x);
        Assert.Equal(0, p.y);

        var pMax = PointerMapper.ToScreenPoint(1.0, 1.0, leftMonitor);
        Assert.Equal(-1, pMax.x);
    }

    [Fact]
    public void ToScreenPoint_handles_monitor_above_primary_with_negative_top()
    {
        var aboveMonitor = new MonitorRect(Left: 0, Top: -1080, Width: 1920, Height: 1080);
        var p = PointerMapper.ToScreenPoint(0.0, 0.0, aboveMonitor);
        Assert.Equal(-1080, p.y);
    }

    [Fact]
    public void ToScreenPoint_handles_different_resolution()
    {
        var uhd = new MonitorRect(Left: 0, Top: 0, Width: 3840, Height: 2160);
        var p = PointerMapper.ToScreenPoint(1.0, 1.0, uhd);
        Assert.Equal(3839, p.x);
        Assert.Equal(2159, p.y);
    }

    [Theory]
    [InlineData(-1.0)]
    [InlineData(2.0)]
    public void ToScreenPoint_clamps_out_of_range_normalized_input(double outOfRange)
    {
        var p = PointerMapper.ToScreenPoint(outOfRange, outOfRange, Primary1080p);
        Assert.InRange(p.x, 0, 1919);
        Assert.InRange(p.y, 0, 1079);
    }

    // --- Pressure ---

    [Theory]
    [InlineData(0.0, 0u)]
    [InlineData(1.0, 1024u)]
    [InlineData(-5.0, 0u)]   // clamped
    [InlineData(5.0, 1024u)] // clamped
    public void ToPressure_scales_and_clamps(double normalized, uint expected)
        => Assert.Equal(expected, PointerMapper.ToPressure(normalized));

    [Fact]
    public void ToPressure_half_scales_to_roughly_half_range()
        => Assert.Equal(512u, PointerMapper.ToPressure(0.5));

    // --- Validation: NaN / Infinity must be rejected, not clamped ---

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void TryValidate_rejects_non_finite_x(double badX)
    {
        var raw = new PenSample(PenPhase.Move, true, badX, 0.5, 0.5, 0, 0);
        Assert.False(PointerMapper.TryValidate(raw, out _));
    }

    [Fact]
    public void TryValidate_clamps_tilt_to_documented_range()
    {
        var raw = new PenSample(PenPhase.Move, true, 0.5, 0.5, 0.5, TiltX: -200, TiltY: 200);
        Assert.True(PointerMapper.TryValidate(raw, out var sanitized));
        Assert.Equal(-90, sanitized.TiltX);
        Assert.Equal(90, sanitized.TiltY);
    }

    [Theory]
    [InlineData(-90)]
    [InlineData(0)]
    [InlineData(90)]
    public void TryValidate_passes_through_in_range_tilt_unchanged(int tilt)
    {
        var raw = new PenSample(PenPhase.Move, true, 0.5, 0.5, 0.5, tilt, tilt);
        Assert.True(PointerMapper.TryValidate(raw, out var sanitized));
        Assert.Equal(tilt, sanitized.TiltX);
    }

    // --- Pointer flags: this is what regressed before (see NativeFlagsTests) ---

    [Fact]
    public void Down_sets_new_incontact_down_flags()
    {
        uint flags = PointerMapper.BuildPointerFlags(PenPhase.Down, inContact: true);
        Assert.Equal(NativeMethods.POINTER_FLAG_DOWN, flags & NativeMethods.POINTER_FLAG_DOWN);
        Assert.Equal(NativeMethods.POINTER_FLAG_INCONTACT, flags & NativeMethods.POINTER_FLAG_INCONTACT);
    }

    [Fact]
    public void Hover_move_does_not_set_incontact()
    {
        uint flags = PointerMapper.BuildPointerFlags(PenPhase.Move, inContact: false);
        Assert.Equal(0u, flags & NativeMethods.POINTER_FLAG_INCONTACT);
        Assert.Equal(NativeMethods.POINTER_FLAG_UPDATE, flags & NativeMethods.POINTER_FLAG_UPDATE);
    }

    [Fact]
    public void Drag_move_sets_incontact()
    {
        uint flags = PointerMapper.BuildPointerFlags(PenPhase.Move, inContact: true);
        Assert.Equal(NativeMethods.POINTER_FLAG_INCONTACT, flags & NativeMethods.POINTER_FLAG_INCONTACT);
    }

    [Fact]
    public void Up_clears_incontact_and_sets_up()
    {
        uint flags = PointerMapper.BuildPointerFlags(PenPhase.Up, inContact: false);
        Assert.Equal(NativeMethods.POINTER_FLAG_UP, flags & NativeMethods.POINTER_FLAG_UP);
        Assert.Equal(0u, flags & NativeMethods.POINTER_FLAG_INCONTACT);
    }
}
