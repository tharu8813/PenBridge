using PenBridge.Models;

namespace PenBridge.Input;

/// <summary>A monitor's bounds on the Windows virtual desktop (physical pixels). Left/Top can be
/// negative — e.g. a monitor positioned to the left of or above the primary monitor.</summary>
public readonly record struct MonitorRect(int Left, int Top, int Width, int Height);

/// <summary>Pure coordinate/flag mapping from a normalized iPad sample to the Win32 pointer
/// struct. No device handles, no counters, no mutable state — PointerInjector owns the parts
/// of the real event (pointerId, frameId) that must persist across calls.</summary>
internal static class PointerMapper
{
    public const int MaxPressure = 1024;
    public const int MinTiltDegrees = -90;
    public const int MaxTiltDegrees = 90;

    /// <summary>Rejects non-finite input (NaN/Infinity from a malformed or hostile client) and
    /// clamps everything else into its valid range. Returns false if the sample must be dropped.</summary>
    public static bool TryValidate(PenSample raw, out PenSample sanitized)
    {
        if (!double.IsFinite(raw.X) || !double.IsFinite(raw.Y) || !double.IsFinite(raw.Pressure))
        {
            sanitized = default;
            return false;
        }

        sanitized = raw with
        {
            X = Math.Clamp(raw.X, 0.0, 1.0),
            Y = Math.Clamp(raw.Y, 0.0, 1.0),
            Pressure = Math.Clamp(raw.Pressure, 0.0, 1.0),
            TiltX = Math.Clamp(raw.TiltX, MinTiltDegrees, MaxTiltDegrees),
            TiltY = Math.Clamp(raw.TiltY, MinTiltDegrees, MaxTiltDegrees),
            Rotation = raw.Rotation is { } rotation ? ((rotation % 360) + 360) % 360 : null,
        };
        return true;
    }

    /// <summary>Normalized (0..1) -> absolute virtual-desktop pixel, using Width-1/Height-1 so a
    /// normalized 1.0 lands on the last valid column/row instead of one pixel past the monitor.</summary>
    public static POINT ToScreenPoint(double normalizedX, double normalizedY, MonitorRect monitor)
    {
        int maxX = Math.Max(monitor.Width - 1, 0);
        int maxY = Math.Max(monitor.Height - 1, 0);
        int px = monitor.Left + (int)Math.Round(Math.Clamp(normalizedX, 0, 1) * maxX);
        int py = monitor.Top + (int)Math.Round(Math.Clamp(normalizedY, 0, 1) * maxY);
        return new POINT { x = px, y = py };
    }

    public static uint ToPressure(double normalizedPressure) =>
        (uint)Math.Round(Math.Clamp(normalizedPressure, 0, 1) * MaxPressure);

    public static uint BuildPointerFlags(PenPhase phase, bool inContact)
    {
        uint flags = NativeMethods.POINTER_FLAG_INRANGE;
        switch (phase)
        {
            case PenPhase.Down:
                flags |= NativeMethods.POINTER_FLAG_NEW | NativeMethods.POINTER_FLAG_INCONTACT
                    | NativeMethods.POINTER_FLAG_DOWN | NativeMethods.POINTER_FLAG_FIRSTBUTTON | NativeMethods.POINTER_FLAG_PRIMARY;
                break;
            case PenPhase.Move:
                flags |= NativeMethods.POINTER_FLAG_UPDATE | NativeMethods.POINTER_FLAG_PRIMARY;
                if (inContact)
                    flags |= NativeMethods.POINTER_FLAG_INCONTACT | NativeMethods.POINTER_FLAG_FIRSTBUTTON;
                break;
            case PenPhase.Up:
                flags |= NativeMethods.POINTER_FLAG_UP | NativeMethods.POINTER_FLAG_PRIMARY;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(phase), phase, "unknown pen phase");
        }
        return flags;
    }

    /// <summary>InjectSyntheticPointerInput expects pixels relative to the virtual screen's
    /// top-left, unlike Screen.Bounds / CopyFromScreen which use the primary screen origin.</summary>
    public static POINT ToInjectionPoint(double normalizedX, double normalizedY,
        MonitorRect monitor, MonitorRect virtualDesktop)
    {
        var point = ToScreenPoint(normalizedX, normalizedY, monitor);
        return new POINT { x = point.x - virtualDesktop.Left, y = point.y - virtualDesktop.Top };
    }

    public static POINTER_TYPE_INFO BuildPenInfo(PenSample sample, MonitorRect monitor,
        MonitorRect virtualDesktop, uint pointerId, uint frameId)
    {
        var penInfo = new POINTER_PEN_INFO
        {
            pointerInfo = new POINTER_INFO
            {
                pointerType = NativeMethods.PT_PEN,
                pointerId = pointerId,
                frameId = frameId,
                pointerFlags = BuildPointerFlags(sample.Phase, sample.InContact),
                ptPixelLocation = ToInjectionPoint(sample.X, sample.Y, monitor, virtualDesktop),
            },
            penFlags = (sample.Eraser ? NativeMethods.PEN_FLAG_ERASER | NativeMethods.PEN_FLAG_INVERTED : 0)
                | (sample.Barrel ? NativeMethods.PEN_FLAG_BARREL : 0),
            penMask = NativeMethods.PEN_MASK_PRESSURE | NativeMethods.PEN_MASK_TILT_X | NativeMethods.PEN_MASK_TILT_Y
                | (sample.Rotation.HasValue ? NativeMethods.PEN_MASK_ROTATION : 0),
            rotation = (uint)(((sample.Rotation.GetValueOrDefault() % 360) + 360) % 360),
            pressure = ToPressure(sample.Pressure),
            tiltX = sample.TiltX,
            tiltY = sample.TiltY,
        };
        return new POINTER_TYPE_INFO { type = NativeMethods.PT_PEN, penInfo = penInfo };
    }

    public static POINTER_TYPE_INFO BuildTouchInfo(PenSample sample, MonitorRect monitor,
        MonitorRect virtualDesktop, uint pointerId, uint frameId, bool primary)
    {
        var screen = ToScreenPoint(sample.X, sample.Y, monitor);
        var injectionPoint = new POINT { x = screen.x - virtualDesktop.Left, y = screen.y - virtualDesktop.Top };
        uint flags = BuildPointerFlags(sample.Phase, sample.InContact);
        if (!primary) flags &= ~NativeMethods.POINTER_FLAG_PRIMARY;
        int radius = 4;
        return new POINTER_TYPE_INFO
        {
            type = NativeMethods.PT_TOUCH,
            touchInfo = new POINTER_TOUCH_INFO
            {
                pointerInfo = new POINTER_INFO
                {
                    pointerType = NativeMethods.PT_TOUCH,
                    pointerId = pointerId,
                    frameId = frameId,
                    pointerFlags = flags,
                    ptPixelLocation = injectionPoint,
                    ptPixelLocationRaw = injectionPoint,
                },
                touchMask = NativeMethods.TOUCH_MASK_CONTACTAREA | NativeMethods.TOUCH_MASK_ORIENTATION | NativeMethods.TOUCH_MASK_PRESSURE,
                rcContact = new RECT { left = injectionPoint.x - radius, top = injectionPoint.y - radius, right = injectionPoint.x + radius, bottom = injectionPoint.y + radius },
                rcContactRaw = new RECT { left = injectionPoint.x - radius, top = injectionPoint.y - radius, right = injectionPoint.x + radius, bottom = injectionPoint.y + radius },
                orientation = 90,
                pressure = sample.InContact ? Math.Max(1u, ToPressure(sample.Pressure)) : 0,
            }
        };
    }
}
