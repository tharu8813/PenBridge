namespace PenBridge.Models;

public enum PenPhase { Down, Move, Up }

/// <summary>
/// One pen sample off the iPad's Pointer Events, normalized 0..1 over its viewport.
/// InContact distinguishes an actual touching drag from an Apple Pencil hover-move (tip not
/// down) — both fire as Phase.Move on the client, so the phase alone can't tell them apart.
/// </summary>
public readonly record struct PenSample(PenPhase Phase, bool InContact, double X, double Y, double Pressure,
    int TiltX, int TiltY, int? Rotation = null, bool Eraser = false, bool Barrel = false,
    int PointerId = 1, bool IsTouch = false)
{
    /// <summary>Bumped whenever the wire format changes, so a mismatched client/server pair
    /// fails with a clear log line instead of silently misinterpreting fields.</summary>
    public const int ProtocolVersion = 1;
}
