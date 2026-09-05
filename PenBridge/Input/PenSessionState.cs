using PenBridge.Models;

namespace PenBridge.Input;

public enum PenState { OutOfRange, Hover, Contact }

/// <summary>
/// Guards one iPad session's down/move/up stream against malformed or out-of-order transitions
/// (dropped WebSocket frames, client races) before anything reaches the injector, and remembers
/// the last sample so a dropped connection can be turned into a clean release instead of leaving
/// Windows thinking the pen is permanently held down.
/// </summary>
public sealed class PenSessionState
{
    private PenState _state = PenState.OutOfRange;
    private PenSample? _lastSample;

    /// <summary>Returns the sample to inject, or null if this one must be dropped
    /// (a duplicate down, or an up with no matching down).</summary>
    public PenSample? Process(PenSample sample)
    {
        switch (sample.Phase)
        {
            case PenPhase.Down:
                if (_state == PenState.Contact)
                    return null; // duplicate down — position still tracks via the moves that follow
                _state = PenState.Contact;
                sample = sample with { InContact = true };
                break;

            case PenPhase.Move:
                // Repair missing transitions instead of emitting UPDATE with an invalid
                // contact state (Windows requires DOWN/UP at contact boundaries).
                if (sample.InContact && _state != PenState.Contact)
                    sample = sample with { Phase = PenPhase.Down };
                else if (!sample.InContact && _state == PenState.Contact)
                    sample = sample with { Phase = PenPhase.Up, Pressure = 0 };
                _state = sample.InContact ? PenState.Contact : PenState.Hover;
                break;

            case PenPhase.Up:
                if (_state != PenState.Contact)
                    return null; // up with no matching down — nothing to release
                _state = PenState.Hover;
                sample = sample with { InContact = false, Pressure = 0 };
                break;
        }

        _lastSample = sample;
        return sample;
    }

    /// <summary>Call when the connection ends. If the pen was mid-stroke, returns a synthetic Up
    /// at the last known position so a dropped Wi-Fi connection, closed Safari tab, or locked
    /// iPad screen doesn't leave Windows believing the pen is still pressed.</summary>
    public PenSample? BuildForcedRelease()
    {
        if (_state != PenState.Contact || _lastSample is not { } last)
            return null;
        _state = PenState.OutOfRange;
        return last with { Phase = PenPhase.Up, InContact = false, Pressure = 0 };
    }
}

/// <summary>Keeps an independent transition state for the Pencil and every simultaneous finger.</summary>
public sealed class PointerSessionState
{
    private readonly Dictionary<(bool Touch, int Id), PenSessionState> _pointers = new();

    public PenSample? Process(PenSample sample)
    {
        var key = (sample.IsTouch, sample.IsTouch ? sample.PointerId : 1);
        if (!_pointers.TryGetValue(key, out var state))
            _pointers[key] = state = new PenSessionState();
        var result = state.Process(sample);
        if (sample.Phase == PenPhase.Up) _pointers.Remove(key);
        return result;
    }

    public IReadOnlyList<PenSample> BuildForcedReleases()
    {
        var releases = _pointers.Values.Select(s => s.BuildForcedRelease()).Where(s => s.HasValue).Select(s => s!.Value).ToArray();
        _pointers.Clear();
        return releases;
    }
}
