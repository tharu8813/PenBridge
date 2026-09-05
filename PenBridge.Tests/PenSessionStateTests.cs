using PenBridge.Input;
using PenBridge.Models;

namespace PenBridge.Tests;

public class PenSessionStateTests
{
    [Fact]
    public void Contact_move_without_down_is_repaired_to_down()
    {
        var state = new PenSessionState();
        Assert.Equal(PenPhase.Down, state.Process(Sample(PenPhase.Move, true))!.Value.Phase);
        Assert.Equal(PenPhase.Move, state.Process(Sample(PenPhase.Move, true))!.Value.Phase);
    }

    [Fact]
    public void Hover_during_contact_releases_before_the_next_tap()
    {
        var state = new PenSessionState();
        state.Process(Sample(PenPhase.Down, true));
        var release = state.Process(Sample(PenPhase.Move, false))!.Value;
        Assert.Equal(PenPhase.Up, release.Phase);
        Assert.Equal(0, release.Pressure);
        Assert.Null(state.BuildForcedRelease());
        Assert.Equal(PenPhase.Down, state.Process(Sample(PenPhase.Down, true))!.Value.Phase);
    }

    [Fact]
    public void Rapid_double_tap_then_drag_keeps_valid_transitions()
    {
        var state = new PenSessionState();
        foreach (var phase in new[] { PenPhase.Down, PenPhase.Up, PenPhase.Down, PenPhase.Move, PenPhase.Up })
            Assert.Equal(phase, state.Process(Sample(phase, phase != PenPhase.Up))!.Value.Phase);
        Assert.Null(state.BuildForcedRelease());
    }

    [Fact]
    public void Forced_release_clears_pressure()
    {
        var state = new PenSessionState();
        state.Process(Sample(PenPhase.Down, true));
        Assert.Equal(0, state.BuildForcedRelease()!.Value.Pressure);
    }

    private static PenSample Sample(PenPhase phase, bool inContact, double x = 0.5, double y = 0.5) =>
        new(phase, inContact, x, y, inContact ? 0.5 : 0.0, 0, 0);

    [Fact]
    public void Normal_down_move_up_sequence_all_pass_through()
    {
        var state = new PenSessionState();
        Assert.NotNull(state.Process(Sample(PenPhase.Down, true)));
        Assert.NotNull(state.Process(Sample(PenPhase.Move, true)));
        Assert.NotNull(state.Process(Sample(PenPhase.Up, false)));
    }

    [Fact]
    public void Hover_moves_before_any_down_pass_through()
    {
        var state = new PenSessionState();
        var result = state.Process(Sample(PenPhase.Move, inContact: false));
        Assert.NotNull(result); // hovering the pencil before touching down is normal, not an error
    }

    [Fact]
    public void Duplicate_down_is_dropped()
    {
        var state = new PenSessionState();
        Assert.NotNull(state.Process(Sample(PenPhase.Down, true)));
        Assert.Null(state.Process(Sample(PenPhase.Down, true))); // second down while already down
    }

    [Fact]
    public void Up_without_a_matching_down_is_dropped()
    {
        var state = new PenSessionState();
        Assert.Null(state.Process(Sample(PenPhase.Up, false)));
    }

    [Fact]
    public void Up_after_hover_only_without_contact_is_dropped()
    {
        var state = new PenSessionState();
        state.Process(Sample(PenPhase.Move, inContact: false));
        Assert.Null(state.Process(Sample(PenPhase.Up, false)));
    }

    [Fact]
    public void Forced_release_is_null_when_pen_was_never_down()
    {
        var state = new PenSessionState();
        state.Process(Sample(PenPhase.Move, inContact: false));
        Assert.Null(state.BuildForcedRelease());
    }

    [Fact]
    public void Forced_release_is_null_after_a_clean_up()
    {
        var state = new PenSessionState();
        state.Process(Sample(PenPhase.Down, true));
        state.Process(Sample(PenPhase.Up, false));
        Assert.Null(state.BuildForcedRelease());
    }

    [Fact]
    public void Disconnect_while_down_produces_an_up_at_the_last_known_position()
    {
        var state = new PenSessionState();
        state.Process(Sample(PenPhase.Down, true, x: 0.2, y: 0.3));
        state.Process(Sample(PenPhase.Move, true, x: 0.7, y: 0.8));

        var release = state.BuildForcedRelease();

        Assert.NotNull(release);
        Assert.Equal(PenPhase.Up, release!.Value.Phase);
        Assert.False(release.Value.InContact);
        Assert.Equal(0.7, release.Value.X);
        Assert.Equal(0.8, release.Value.Y);
    }

    [Fact]
    public void Forced_release_only_fires_once()
    {
        var state = new PenSessionState();
        state.Process(Sample(PenPhase.Down, true));
        Assert.NotNull(state.BuildForcedRelease());
        Assert.Null(state.BuildForcedRelease()); // already released — must not double-fire
    }
}
