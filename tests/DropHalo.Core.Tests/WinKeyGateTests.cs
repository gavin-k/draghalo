using DropHalo.Core.Input;
using DropHalo.Core.Settings;
using DropHalo.Core.Wheel;
using FluentAssertions;

namespace DropHalo.Core.Tests;

public class WinKeyGateTests
{
    private readonly WinKeyGate _gate = new(KeyBindings.Default);

    [Fact]
    public void Swallows_down_and_up_during_a_drag()
    {
        _gate.Swallow(isDown: true, dragging: true).Should().BeTrue();
        _gate.Swallow(isDown: false, dragging: true).Should().BeTrue();
    }

    [Fact]
    public void Passes_everything_outside_a_drag()
    {
        _gate.Swallow(true, false).Should().BeFalse();
        _gate.Swallow(false, false).Should().BeFalse();
    }

    [Fact]
    public void Passes_the_up_of_a_press_that_began_before_the_drag()
    {
        _gate.Swallow(true, false).Should().BeFalse();
        _gate.Swallow(false, true).Should().BeFalse();
    }

    [Fact]
    public void A_passed_down_keeps_its_repeats_and_up_passed_when_a_drag_starts()
    {
        _gate.Swallow(true, false).Should().BeFalse();
        _gate.Swallow(true, true).Should().BeFalse();
        _gate.Swallow(false, true).Should().BeFalse();
    }

    [Theory]
    [InlineData(0x5B, 0x5C)]
    [InlineData(0x5C, 0x5B)]
    public void Each_physical_Win_key_keeps_the_decision_of_its_own_down(int beforeDrag, int duringDrag)
    {
        _gate.Swallow(true, false, beforeDrag).Should().BeFalse();
        _gate.Swallow(true, true, duringDrag).Should().BeTrue();

        _gate.Swallow(false, true, beforeDrag).Should().BeFalse();
        _gate.Swallow(false, true, duringDrag).Should().BeTrue();
    }

    [Theory]
    [InlineData(0x5B, 0x5C)]
    [InlineData(0x5C, 0x5B)]
    public void Both_swallowed_physical_Win_keys_stay_swallowed_until_their_own_up(int first, int second)
    {
        _gate.Swallow(true, true, first).Should().BeTrue();
        _gate.Swallow(true, true, second).Should().BeTrue();
        _gate.Swallow(false, false, first).Should().BeTrue();
        _gate.Rebind(KeyBindings.Default with { Bindings = [new(WheelMode.Convert, Modifiers.Shift)] });
        _gate.Swallow(true, false, second).Should().BeTrue();
        _gate.Swallow(false, false, second).Should().BeTrue();
    }

    [Fact]
    public void Keeps_swallowing_a_press_after_the_drag_ends()
    {
        _gate.Swallow(true, true).Should().BeTrue();
        _gate.Swallow(true, false).Should().BeTrue();
        _gate.Swallow(false, false).Should().BeTrue();
        _gate.Swallow(true, false).Should().BeFalse();
    }

    [Fact]
    public void Rebinding_away_from_win_still_swallows_the_up_of_a_swallowed_down()
    {
        _gate.Swallow(true, true).Should().BeTrue();
        _gate.Rebind(KeyBindings.Default with { Bindings = [new(WheelMode.Convert, Modifiers.Shift)] });

        _gate.Swallow(false, true).Should().BeTrue();
        _gate.Swallow(true, true).Should().BeFalse();
        _gate.Swallow(false, true).Should().BeFalse();
    }

    [Fact]
    public void Pruning_keeps_a_swallowed_press_because_the_system_never_saw_it_go_down()
    {
        _gate.Swallow(true, true, 0x5B).Should().BeTrue();

        // Windows reports a swallowed key as up for the whole press, so asking it proves nothing.
        _gate.PruneReleased(_ => false);

        _gate.HasHeldKeys.Should().BeTrue();
        _gate.Swallow(true, false, 0x5B).Should().BeTrue("the repeat belongs to the swallowed press");
        _gate.Swallow(false, false, 0x5B).Should().BeTrue("a lone key-up would reach Windows");
    }

    [Fact]
    public void Never_swallows_when_win_is_not_bound()
    {
        var gate = new WinKeyGate(KeyBindings.Default with { Bindings = [new(WheelMode.Convert, Modifiers.Shift)] });

        gate.Swallow(true, true).Should().BeFalse();
        gate.Swallow(false, true).Should().BeFalse();
    }
}
