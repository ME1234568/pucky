using Pucky.Core.Mapping;

namespace Pucky.App.Output;

internal static class DesktopKeyDiff
{
    /// <summary>
    /// Emits releases (main keys before modifiers) and then presses
    /// (modifiers before main keys), so chords such as Ctrl+C are delivered
    /// in the order applications expect.
    /// </summary>
    public static void Apply(KeySet previous, KeySet current, Action<KeyboardKey, bool> emit)
    {
        if (previous == current)
        {
            return;
        }

        var released = previous.Except(current);
        var pressed = current.Except(previous);
        foreach (var key in released.Keys())
        {
            if (!KeyboardKeys.IsModifier(key)) emit(key, false);
        }
        foreach (var key in released.Keys())
        {
            if (KeyboardKeys.IsModifier(key)) emit(key, false);
        }
        foreach (var key in pressed.Keys())
        {
            if (KeyboardKeys.IsModifier(key)) emit(key, true);
        }
        foreach (var key in pressed.Keys())
        {
            if (!KeyboardKeys.IsModifier(key)) emit(key, true);
        }
    }
}
