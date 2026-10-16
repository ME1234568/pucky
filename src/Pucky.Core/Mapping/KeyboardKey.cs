namespace Pucky.Core.Mapping;

/// <summary>
/// Platform-neutral keyboard keys and mouse buttons that a controller button
/// can emit. Values are stable bit indexes for <see cref="KeySet"/>, so new
/// keys must be appended rather than inserted.
/// </summary>
public enum KeyboardKey : byte
{
    None = 0,
    A, B, C, D, E, F, G, H, I, J, K, L, M,
    N, O, P, Q, R, S, T, U, V, W, X, Y, Z,
    D0, D1, D2, D3, D4, D5, D6, D7, D8, D9,
    F1, F2, F3, F4, F5, F6, F7, F8, F9, F10, F11, F12,
    Escape, Enter, Tab, Space, Backspace, CapsLock,
    LeftShift, RightShift, LeftCtrl, RightCtrl, LeftAlt, RightAlt, LeftMeta, RightMeta,
    Up, Down, Left, Right,
    Home, End, PageUp, PageDown, Insert, Delete,
    Minus, Equals, LeftBracket, RightBracket, Backslash, Semicolon, Apostrophe, Grave,
    Comma, Period, Slash,
    PrintScreen, ScrollLock, Pause,
    VolumeUp, VolumeDown, VolumeMute, MediaPlayPause, MediaNext, MediaPrevious, MediaStop,
    MouseLeft, MouseRight, MouseMiddle, MouseBack, MouseForward,
    WheelUp, WheelDown, WheelLeft, WheelRight
}

/// <summary>An allocation-free set of held <see cref="KeyboardKey"/> values.</summary>
public readonly record struct KeySet(ulong Low, ulong High)
{
    public static readonly KeySet Empty = default;

    public bool IsEmpty => Low == 0 && High == 0;

    public bool Contains(KeyboardKey key)
    {
        var index = (int)key;
        return index < 64
            ? (Low & (1UL << index)) != 0
            : (High & (1UL << (index - 64))) != 0;
    }

    public KeySet With(KeyboardKey key)
    {
        if (key == KeyboardKey.None)
        {
            return this;
        }

        var index = (int)key;
        return index < 64
            ? this with { Low = Low | (1UL << index) }
            : this with { High = High | (1UL << (index - 64)) };
    }

    public KeySet Union(KeySet other) => new(Low | other.Low, High | other.High);

    public KeySet Except(KeySet other) => new(Low & ~other.Low, High & ~other.High);

    public IEnumerable<KeyboardKey> Keys()
    {
        for (var index = 1; index < 128; index++)
        {
            var bit = index < 64
                ? (Low >> index) & 1
                : (High >> (index - 64)) & 1;
            if (bit != 0)
            {
                yield return (KeyboardKey)index;
            }
        }
    }

    public static KeySet Of(params KeyboardKey[] keys)
    {
        var set = Empty;
        foreach (var key in keys)
        {
            set = set.With(key);
        }
        return set;
    }
}

public static class KeyboardKeys
{
    public static bool IsModifier(KeyboardKey key) =>
        key is KeyboardKey.LeftShift or KeyboardKey.RightShift or
            KeyboardKey.LeftCtrl or KeyboardKey.RightCtrl or
            KeyboardKey.LeftAlt or KeyboardKey.RightAlt or
            KeyboardKey.LeftMeta or KeyboardKey.RightMeta;

    public static bool IsMouseButton(KeyboardKey key) =>
        key is >= KeyboardKey.MouseLeft and <= KeyboardKey.MouseForward;

    /// <summary>Wheel "keys" emit one notch when they are first pressed.</summary>
    public static bool IsWheel(KeyboardKey key) =>
        key is >= KeyboardKey.WheelUp and <= KeyboardKey.WheelRight;
}
