using System.ComponentModel;
using System.Runtime.InteropServices;
using PenBridge.Logging;
using PenBridge.Models;

namespace PenBridge.Input;

/// <summary>
/// Owns one synthetic pen device handle. Not thread-safe by design: PenBridgeServer only ever
/// allows a single active iPad session, so at most one thread injects at a time. All Win32
/// return values are checked — a failure is logged with the real Win32 error, never swallowed.
/// </summary>
public sealed class PointerInjector : IDisposable
{
    private const uint PenPointerId = 1;
    private const uint MaxTouchContacts = 10;
    private readonly ILog _log;
    private IntPtr _penDevice = IntPtr.Zero;
    private IntPtr _touchDevice = IntPtr.Zero;
    private readonly Dictionary<int, uint> _touchIds = new();
    private readonly Dictionary<int, PenSample> _activeTouches = new();
    private readonly Queue<uint> _availableTouchIds = new(Enumerable.Range(1, (int)MaxTouchContacts).Select(i => (uint)i));
    private uint _frameId;

    public PointerInjector(ILog log) => _log = log;

    public bool TryOpen(out string? error)
    {
        if (_penDevice != IntPtr.Zero && _touchDevice != IntPtr.Zero) { error = null; return true; }
        _frameId = 0;
        _penDevice = NativeMethods.CreateSyntheticPointerDevice(NativeMethods.PT_PEN, maxCount: 1, NativeMethods.POINTER_FEEDBACK_DEFAULT);
        if (_penDevice == IntPtr.Zero)
        {
            error = Win32ErrorMessage();
            _log.Error($"CreateSyntheticPointerDevice failed: {error}");
            return false;
        }
        _touchDevice = NativeMethods.CreateSyntheticPointerDevice(NativeMethods.PT_TOUCH, MaxTouchContacts, NativeMethods.POINTER_FEEDBACK_DEFAULT);
        if (_touchDevice == IntPtr.Zero)
        {
            error = Win32ErrorMessage();
            _log.Error($"CreateSyntheticPointerDevice(터치) failed: {error}");
            NativeMethods.DestroySyntheticPointerDevice(_penDevice);
            _penDevice = IntPtr.Zero;
            return false;
        }
        ResetTouchIds();
        error = null;
        return true;
    }

    public bool Inject(PenSample sample, MonitorRect monitor)
    {
        IntPtr device = sample.IsTouch ? _touchDevice : _penDevice;
        if (device == IntPtr.Zero)
        {
            _log.Warn("Inject called before the pointer device was opened — dropping sample.");
            return false;
        }

        var bounds = System.Windows.Forms.SystemInformation.VirtualScreen;
        var virtualDesktop = new MonitorRect(bounds.Left, bounds.Top, bounds.Width, bounds.Height);
        if (sample.IsTouch)
            return InjectTouchFrame(sample, monitor, virtualDesktop);

        var info = PointerMapper.BuildPenInfo(sample, monitor, virtualDesktop, PenPointerId, ++_frameId);
        bool ok = NativeMethods.InjectSyntheticPointerInput(device, new[] { info }, 1);
        if (!ok)
            _log.Warn($"InjectSyntheticPointerInput failed: {Win32ErrorMessage()}");
        return ok;
    }

    private bool InjectTouchFrame(PenSample changed, MonitorRect monitor, MonitorRect virtualDesktop)
    {
        if (!_touchIds.TryGetValue(changed.PointerId, out uint changedNativeId))
        {
            if (changed.Phase == PenPhase.Up) return true;
            if (_availableTouchIds.Count == 0) { _log.Warn("동시 터치 10개를 초과한 입력을 무시했습니다."); return true; }
            changedNativeId = _availableTouchIds.Dequeue();
            _touchIds[changed.PointerId] = changedNativeId;
        }

        uint frame = ++_frameId;
        var contacts = ComposeTouchFrame(changed, changedNativeId, _activeTouches, _touchIds);
        var infos = contacts.Select(pair => PointerMapper.BuildTouchInfo(pair.Sample, monitor, virtualDesktop,
            pair.NativeId, frame, pair.NativeId == 1)).ToArray();

        bool ok = NativeMethods.InjectSyntheticPointerInput(_touchDevice, infos, (uint)infos.Length);
        if (!ok)
        {
            _log.Warn($"InjectSyntheticPointerInput(멀티 터치 프레임) failed: {Win32ErrorMessage()}");
            return false;
        }

        if (changed.Phase == PenPhase.Up)
        {
            _activeTouches.Remove(changed.PointerId);
            _touchIds.Remove(changed.PointerId);
            _availableTouchIds.Enqueue(changedNativeId);
            if (_activeTouches.Count == 0) ResetTouchIds();
        }
        else
            _activeTouches[changed.PointerId] = changed with { InContact = true };
        return true;
    }

    internal static (PenSample Sample, uint NativeId)[] ComposeTouchFrame(PenSample changed, uint changedNativeId,
        IReadOnlyDictionary<int, PenSample> activeTouches, IReadOnlyDictionary<int, uint> nativeIds) =>
        activeTouches.Where(pair => pair.Key != changed.PointerId)
            .Select(pair => (Sample: pair.Value with { Phase = PenPhase.Move, InContact = true }, NativeId: nativeIds[pair.Key]))
            .Append((Sample: changed, NativeId: changedNativeId))
            .OrderBy(pair => pair.NativeId)
            .ToArray();

    private static string Win32ErrorMessage()
    {
        int code = Marshal.GetLastWin32Error();
        return $"{new Win32Exception(code).Message} (0x{code:X8})";
    }

    public void Dispose()
    {
        foreach (var device in new[] { _penDevice, _touchDevice })
            if (device != IntPtr.Zero && !NativeMethods.DestroySyntheticPointerDevice(device))
                _log.Warn($"DestroySyntheticPointerDevice failed: {Win32ErrorMessage()}");
        _penDevice = _touchDevice = IntPtr.Zero;
        ResetTouchIds();
    }

    private void ResetTouchIds()
    {
        _touchIds.Clear();
        _activeTouches.Clear();
        _availableTouchIds.Clear();
        foreach (uint id in Enumerable.Range(1, (int)MaxTouchContacts).Select(i => (uint)i)) _availableTouchIds.Enqueue(id);
    }
}
