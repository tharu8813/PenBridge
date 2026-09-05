using System.Runtime.InteropServices;

namespace PenBridge.Input;

/// <summary>
/// Raw Win32 declarations for the Synthetic Pointer Input API (CreateSyntheticPointerDevice /
/// InjectSyntheticPointerInput), introduced in Windows 10 1809. No app logic here — see
/// PointerMapper for coordinate/flag mapping and PointerInjector for lifecycle + the actual calls.
///
/// POINTER_TYPE_INFO's union starts at byte offset 8 because POINTER_INFO contains pointer-sized
/// fields (HANDLE sourceDevice, HWND hwndTarget) that require 8-byte alignment — true on x64 and
/// arm64 (both use 8-byte pointers), but the union would start at offset 4 on x86. Rather than
/// hand-packing a byte[] to be arch-generic, this project is restricted to 64-bit (PlatformTarget
/// x64 in the .csproj + the Environment.Is64BitProcess guard in Program.cs) so offset 8 always holds.
/// </summary>
internal static class NativeMethods
{
    public const int PT_PEN = 3;
    public const int PT_TOUCH = 2;
    public const int POINTER_FEEDBACK_DEFAULT = 1;

    // POINTER_FLAGS (winuser.h). Verified against the documented enum — a prior version of this
    // file had DOWN/UPDATE/UP shifted by one slot, colliding with POINTER_FLAG_WHEEL/HWHEEL.
    public const uint POINTER_FLAG_NEW = 0x00000001;
    public const uint POINTER_FLAG_INRANGE = 0x00000002;
    public const uint POINTER_FLAG_INCONTACT = 0x00000004;
    public const uint POINTER_FLAG_FIRSTBUTTON = 0x00000010;
    public const uint POINTER_FLAG_PRIMARY = 0x00002000;
    public const uint POINTER_FLAG_DOWN = 0x00010000;
    public const uint POINTER_FLAG_UPDATE = 0x00020000;
    public const uint POINTER_FLAG_UP = 0x00040000;

    // PEN_MASK (winuser.h)
    public const uint PEN_MASK_PRESSURE = 0x00000001;
    public const uint PEN_MASK_ROTATION = 0x00000002;
    public const uint PEN_FLAG_BARREL = 0x00000001;
    public const uint PEN_FLAG_INVERTED = 0x00000002;
    public const uint PEN_FLAG_ERASER = 0x00000004;
    public const uint PEN_MASK_TILT_X = 0x00000004;
    public const uint PEN_MASK_TILT_Y = 0x00000008;
    public const uint TOUCH_MASK_CONTACTAREA = 0x00000001;
    public const uint TOUCH_MASK_ORIENTATION = 0x00000002;
    public const uint TOUCH_MASK_PRESSURE = 0x00000004;

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr CreateSyntheticPointerDevice(int pointerType, uint maxCount, int mode);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool InjectSyntheticPointerInput(IntPtr device, [In] POINTER_TYPE_INFO[] pointerInfo, uint count);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool DestroySyntheticPointerDevice(IntPtr device);
}

[StructLayout(LayoutKind.Sequential)]
internal struct POINT
{
    public int x;
    public int y;
}

[StructLayout(LayoutKind.Sequential)]
internal struct POINTER_INFO
{
    public int pointerType;
    public uint pointerId;
    public uint frameId;
    public uint pointerFlags;
    public IntPtr sourceDevice;
    public IntPtr hwndTarget;
    public POINT ptPixelLocation;
    public POINT ptHimetricLocation;
    public POINT ptPixelLocationRaw;
    public POINT ptHimetricLocationRaw;
    public uint dwTime;
    public uint historyCount;
    public int InputData;
    public uint dwKeyStates;
    public ulong PerformanceCount;
    public int ButtonChangeType;
}

[StructLayout(LayoutKind.Sequential)]
internal struct POINTER_PEN_INFO
{
    public POINTER_INFO pointerInfo;
    public uint penFlags;
    public uint penMask;
    public uint pressure;
    public uint rotation;
    public int tiltX;
    public int tiltY;
}

[StructLayout(LayoutKind.Sequential)]
internal struct RECT
{
    public int left;
    public int top;
    public int right;
    public int bottom;
}

[StructLayout(LayoutKind.Sequential)]
internal struct POINTER_TOUCH_INFO
{
    public POINTER_INFO pointerInfo;
    public uint touchFlags;
    public uint touchMask;
    public RECT rcContact;
    public RECT rcContactRaw;
    public uint orientation;
    public uint pressure;
}

[StructLayout(LayoutKind.Explicit)]
internal struct POINTER_TYPE_INFO
{
    [FieldOffset(0)] public int type;
    [FieldOffset(8)] public POINTER_PEN_INFO penInfo;
    [FieldOffset(8)] public POINTER_TOUCH_INFO touchInfo;
}
