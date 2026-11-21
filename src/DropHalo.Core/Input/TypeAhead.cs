using DropHalo.Core.Settings;

namespace DropHalo.Core.Input;

/// <summary>
/// Letters and digits typed while the Convert wheel is open, for searching every format the file can
/// become. The hook thread feeds it each non-modifier key event and swallows the ones it takes, so the
/// drag source and the input method never see them. Not thread-safe apart from <see cref="Listening"/>;
/// call <see cref="Swallow"/> from one thread.
/// </summary>
/// <remarks>
/// Like <see cref="WinKeyGate"/>, down and up are kept as a pair: a press whose down was taken has its
/// repeats and its up swallowed too, even after the wheel closed, and a press whose down reached Windows
/// is passed through to its up. Keys held with Ctrl, Alt or Win are shortcuts and are left alone.
/// </remarks>
public sealed class TypeAhead
{
    /// <summary>What <see cref="Typed"/> carries for Backspace.</summary>
    public const char Backspace = '\b';

    private const byte Up = 0;
    private const byte Passed = 1;
    private const byte Taken = 2;

    private readonly byte[] _keys = new byte[256];
    private int _activeCount;
    private volatile bool _listening;

    /// <summary>Raised on the calling thread with a lower-case letter, a digit or <see cref="Backspace"/>, auto-repeats included.</summary>
    public event Action<char>? Typed;

    /// <summary>True when at least one search key is currently tracked between key-down and key-up.</summary>
    public bool HasHeldKeys => _activeCount > 0;

    /// <summary>Whether keys are taken now; set from any thread while the Convert wheel is open.</summary>
    public bool Listening
    {
        get => _listening;
        set => _listening = value;
    }

    /// <summary>True if the hook should swallow this key event.</summary>
    /// <param name="vk">The virtual-key code.</param>
    /// <param name="held">The modifiers down when the key event came.</param>
    public bool Swallow(int vk, bool isDown, Modifiers held)
    {
        if (CharOf(vk) is not { } c)
        {
            return false;
        }

        if (!isDown)
        {
            var taken = _keys[vk] == Taken;
            if (_keys[vk] != Up)
            {
                _keys[vk] = Up;
                _activeCount--;
            }

            return taken;
        }

        if (_keys[vk] == Up)
        {
            _keys[vk] = _listening && (held & (Modifiers.Ctrl | Modifiers.Alt | Modifiers.Win)) == 0 ? Taken : Passed;
            _activeCount++;
        }

        if (_keys[vk] != Taken)
        {
            return false;
        }

        if (_listening)
        {
            Typed?.Invoke(c);
        }

        return true;
    }

    /// <summary>Resets keys whose key-up was lost to the secure desktop.</summary>
    /// <remarks>
    /// Only keys that reached Windows are checked. Windows never saw a taken key-down, so it reports
    /// that key as up for the whole press; resetting it would let its key-up through alone.
    /// </remarks>
    public void PruneReleased(Func<int, bool> isPhysicallyDown)
    {
        if (_activeCount == 0)
        {
            return;
        }

        for (var vk = 0; vk < _keys.Length && _activeCount > 0; vk++)
        {
            if (_keys[vk] == Passed && !isPhysicallyDown(vk))
            {
                _keys[vk] = Up;
                _activeCount--;
            }
        }
    }

    /// <summary>The character a key types into a search, or null for keys a search ignores.</summary>
    private static char? CharOf(int vk) => vk switch
    {
        0x08 => Backspace,
        >= 0x30 and <= 0x39 => (char)('0' + vk - 0x30),
        >= 0x41 and <= 0x5A => (char)('a' + vk - 0x41),
        >= 0x60 and <= 0x69 => (char)('0' + vk - 0x60),
        _ => null,
    };
}
