using DropHalo.Core.Settings;

namespace DropHalo.Core.Input;

/// <summary>
/// Decides whether the keyboard hook swallows a Win key event so the Start menu stays shut while a
/// drag is in progress and Win is bound to a wheel (ADR-0002 revision, item 4).
/// </summary>
/// <remarks>
/// Down and up are swallowed as a pair: a press whose down was swallowed has its repeats and its up
/// swallowed too, even if the drag ended meanwhile, and an up whose down reached Windows is passed
/// through. Otherwise Windows sees a lone down and believes Win is stuck, or a lone up and opens Start.
/// </remarks>
public sealed class WinKeyGate(KeyBindings bindings)
{
    private volatile bool _winBound = WinBound(bindings);
    private readonly Dictionary<int, bool> _pressed = [];

    /// <summary>Swaps the bindings, from any thread. A press already being swallowed is swallowed to its key-up.</summary>
    public void Rebind(KeyBindings bindings) => _winBound = WinBound(bindings);

    /// <summary>True if the hook should swallow this Win key event instead of passing it on.</summary>
    /// <param name="physicalKey">The Shell's left/right virtual-key code; zero for a logical transition.</param>
    public bool Swallow(bool isDown, bool dragging, int physicalKey = 0)
    {
        if (isDown)
        {
            _pressed.TryAdd(physicalKey, _winBound && dragging);
            return _pressed[physicalKey];
        }

        return _pressed.Remove(physicalKey, out var swallowed) && swallowed;
    }

    private static bool WinBound(KeyBindings bindings) => bindings.Bindings.Any(b => (b.Keys & Modifiers.Win) != 0);
}
