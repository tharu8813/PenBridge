using PenBridge.Input;

namespace PenBridge.Tests;

/// <summary>
/// Pins the raw Win32 POINTER_FLAGS values against the documented winuser.h enum. A previous
/// version of NativeMethods had DOWN/UPDATE/UP shifted by one slot, silently colliding with
/// POINTER_FLAG_WHEEL/HWHEEL — this test exists specifically so that regression can't happen
/// again unnoticed.
/// </summary>
public class NativeFlagsTests
{
    [Fact]
    public void Down_matches_documented_value()
        => Assert.Equal(0x00010000u, NativeMethods.POINTER_FLAG_DOWN);

    [Fact]
    public void Update_matches_documented_value()
        => Assert.Equal(0x00020000u, NativeMethods.POINTER_FLAG_UPDATE);

    [Fact]
    public void Up_matches_documented_value()
        => Assert.Equal(0x00040000u, NativeMethods.POINTER_FLAG_UP);

    [Fact]
    public void Down_update_up_do_not_collide_with_wheel_or_hwheel()
    {
        const uint POINTER_FLAG_WHEEL = 0x00080000;
        const uint POINTER_FLAG_HWHEEL = 0x00100000;

        Assert.NotEqual(POINTER_FLAG_WHEEL, NativeMethods.POINTER_FLAG_DOWN);
        Assert.NotEqual(POINTER_FLAG_WHEEL, NativeMethods.POINTER_FLAG_UPDATE);
        Assert.NotEqual(POINTER_FLAG_WHEEL, NativeMethods.POINTER_FLAG_UP);
        Assert.NotEqual(POINTER_FLAG_HWHEEL, NativeMethods.POINTER_FLAG_DOWN);
        Assert.NotEqual(POINTER_FLAG_HWHEEL, NativeMethods.POINTER_FLAG_UPDATE);
        Assert.NotEqual(POINTER_FLAG_HWHEEL, NativeMethods.POINTER_FLAG_UP);
    }
}
