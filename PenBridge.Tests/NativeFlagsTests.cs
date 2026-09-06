using PenBridge.Input;
using System.Runtime.InteropServices;

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
    public void Native_pointer_struct_layout_matches_winuser_x64_abi()
    {
        Assert.Equal(8, IntPtr.Size);
        Assert.Equal(96, Marshal.SizeOf<POINTER_INFO>());
        Assert.Equal(120, Marshal.SizeOf<POINTER_PEN_INFO>());
        Assert.Equal(144, Marshal.SizeOf<POINTER_TOUCH_INFO>());
        Assert.Equal(152, Marshal.SizeOf<POINTER_TYPE_INFO>());
    }

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
