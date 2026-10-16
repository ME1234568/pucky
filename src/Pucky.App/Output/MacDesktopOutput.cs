using System.Runtime.InteropServices;
using Pucky.Core.Mapping;

namespace Pucky.App.Output;

public sealed class MacDesktopOutput : IDesktopOutput
{
    private const string ApplicationServices =
        "/System/Library/Frameworks/ApplicationServices.framework/ApplicationServices";
    private const uint HidEventTap = 0;
    private const uint LeftMouseDown = 1;
    private const uint LeftMouseUp = 2;
    private const uint RightMouseDown = 3;
    private const uint RightMouseUp = 4;
    private const uint MouseMoved = 5;
    private const uint LeftMouseDragged = 6;
    private const uint RightMouseDragged = 7;
    private const uint OtherMouseDown = 25;
    private const uint OtherMouseUp = 26;
    private const uint OtherMouseDragged = 27;
    private const uint ScrollUnitPixel = 0;
    private const int ScrollPixelsPerNotch = 10;

    private KeySet _held;
    private ulong _modifierFlags;
    private float _wheelRemainder;
    private float _horizontalWheelRemainder;

    public string Name => "macOS Quartz events (mouse + keyboard)";
    public bool IsAvailable => true;

    public void Submit(DesktopState state)
    {
        var held = state.HeldKeys;
        if (Math.Abs(state.MouseX) > 0.01f || Math.Abs(state.MouseY) > 0.01f)
        {
            var position = CurrentPosition();
            var (type, button) =
                held.Contains(KeyboardKey.MouseLeft) ? (LeftMouseDragged, 0u)
                : held.Contains(KeyboardKey.MouseRight) ? (RightMouseDragged, 1u)
                : held.Contains(KeyboardKey.MouseMiddle) ? (OtherMouseDragged, 2u)
                : (MouseMoved, 0u);
            Post(CGEventCreateMouseEvent(
                IntPtr.Zero,
                type,
                new CGPoint(position.X + state.MouseX, position.Y + state.MouseY),
                button));
        }

        DesktopKeyDiff.Apply(_held, held, Emit);
        _held = held;

        _wheelRemainder += state.ScrollY * ScrollPixelsPerNotch;
        _horizontalWheelRemainder += state.ScrollX * ScrollPixelsPerNotch;
        var wheel = (int)MathF.Truncate(_wheelRemainder);
        var horizontalWheel = (int)MathF.Truncate(_horizontalWheelRemainder);
        _wheelRemainder -= wheel;
        _horizontalWheelRemainder -= horizontalWheel;
        if (wheel != 0 || horizontalWheel != 0)
        {
            Scroll(wheel, horizontalWheel);
        }
    }

    public void Dispose()
    {
        if (!_held.IsEmpty)
        {
            Submit(DesktopState.Idle);
        }
    }

    private void Emit(KeyboardKey key, bool down)
    {
        switch (key)
        {
            case KeyboardKey.MouseLeft:
                PostMouseButton(down ? LeftMouseDown : LeftMouseUp, 0);
                return;
            case KeyboardKey.MouseRight:
                PostMouseButton(down ? RightMouseDown : RightMouseUp, 1);
                return;
            case KeyboardKey.MouseMiddle:
                PostMouseButton(down ? OtherMouseDown : OtherMouseUp, 2);
                return;
            case KeyboardKey.MouseBack:
                PostMouseButton(down ? OtherMouseDown : OtherMouseUp, 3);
                return;
            case KeyboardKey.MouseForward:
                PostMouseButton(down ? OtherMouseDown : OtherMouseUp, 4);
                return;
            case KeyboardKey.WheelUp:
                if (down) Scroll(ScrollPixelsPerNotch, 0);
                return;
            case KeyboardKey.WheelDown:
                if (down) Scroll(-ScrollPixelsPerNotch, 0);
                return;
            case KeyboardKey.WheelRight:
                if (down) Scroll(0, ScrollPixelsPerNotch);
                return;
            case KeyboardKey.WheelLeft:
                if (down) Scroll(0, -ScrollPixelsPerNotch);
                return;
        }

        if (!MacKeyCodes.TryGet(key, out var code))
        {
            return;
        }

        var modifier = MacKeyCodes.ModifierFlag(key);
        if (modifier != 0)
        {
            _modifierFlags = down ? _modifierFlags | modifier : _modifierFlags & ~modifier;
        }

        var keyEvent = CGEventCreateKeyboardEvent(IntPtr.Zero, code, down);
        if (keyEvent == IntPtr.Zero)
        {
            return;
        }
        // Synthetic key events do not inherit modifier state, so chords
        // must carry the currently held modifier flags explicitly.
        CGEventSetFlags(keyEvent, _modifierFlags);
        Post(keyEvent);
    }

    private static void PostMouseButton(uint type, uint button) =>
        Post(CGEventCreateMouseEvent(IntPtr.Zero, type, CurrentPosition(), button));

    private static void Scroll(int vertical, int horizontal) =>
        // CGEventCreateScrollWheelEvent is variadic, which P/Invoke cannot
        // call correctly on Apple Silicon; the "2" variant takes fixed args.
        Post(CGEventCreateScrollWheelEvent2(IntPtr.Zero, ScrollUnitPixel, 2, vertical, horizontal, 0));

    private static CGPoint CurrentPosition()
    {
        var current = CGEventCreate(IntPtr.Zero);
        if (current == IntPtr.Zero)
        {
            return default;
        }
        var position = CGEventGetLocation(current);
        CFRelease(current);
        return position;
    }

    private static void Post(IntPtr eventRef)
    {
        if (eventRef == IntPtr.Zero)
        {
            return;
        }
        CGEventPost(HidEventTap, eventRef);
        CFRelease(eventRef);
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct CGPoint(double X, double Y);

    [DllImport(ApplicationServices)]
    private static extern IntPtr CGEventCreate(IntPtr source);

    [DllImport(ApplicationServices)]
    private static extern CGPoint CGEventGetLocation(IntPtr eventRef);

    [DllImport(ApplicationServices)]
    private static extern IntPtr CGEventCreateMouseEvent(
        IntPtr source,
        uint mouseType,
        CGPoint position,
        uint button);

    [DllImport(ApplicationServices)]
    private static extern IntPtr CGEventCreateKeyboardEvent(
        IntPtr source,
        ushort virtualKey,
        [MarshalAs(UnmanagedType.I1)] bool keyDown);

    [DllImport(ApplicationServices)]
    private static extern void CGEventSetFlags(IntPtr eventRef, ulong flags);

    [DllImport(ApplicationServices)]
    private static extern IntPtr CGEventCreateScrollWheelEvent2(
        IntPtr source,
        uint units,
        uint wheelCount,
        int wheel1,
        int wheel2,
        int wheel3);

    [DllImport(ApplicationServices)]
    private static extern void CGEventPost(uint tap, IntPtr eventRef);

    [DllImport("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation")]
    private static extern void CFRelease(IntPtr value);
}

internal static class MacKeyCodes
{
    private const ulong ShiftFlag = 0x00020000;
    private const ulong ControlFlag = 0x00040000;
    private const ulong AlternateFlag = 0x00080000;
    private const ulong CommandFlag = 0x00100000;

    private static readonly Dictionary<KeyboardKey, ushort> Codes = new()
    {
        [KeyboardKey.A] = 0x00, [KeyboardKey.S] = 0x01, [KeyboardKey.D] = 0x02,
        [KeyboardKey.F] = 0x03, [KeyboardKey.H] = 0x04, [KeyboardKey.G] = 0x05,
        [KeyboardKey.Z] = 0x06, [KeyboardKey.X] = 0x07, [KeyboardKey.C] = 0x08,
        [KeyboardKey.V] = 0x09, [KeyboardKey.B] = 0x0B, [KeyboardKey.Q] = 0x0C,
        [KeyboardKey.W] = 0x0D, [KeyboardKey.E] = 0x0E, [KeyboardKey.R] = 0x0F,
        [KeyboardKey.Y] = 0x10, [KeyboardKey.T] = 0x11, [KeyboardKey.O] = 0x1F,
        [KeyboardKey.U] = 0x20, [KeyboardKey.I] = 0x22, [KeyboardKey.P] = 0x23,
        [KeyboardKey.L] = 0x25, [KeyboardKey.J] = 0x26, [KeyboardKey.K] = 0x28,
        [KeyboardKey.N] = 0x2D, [KeyboardKey.M] = 0x2E,
        [KeyboardKey.D1] = 0x12, [KeyboardKey.D2] = 0x13, [KeyboardKey.D3] = 0x14,
        [KeyboardKey.D4] = 0x15, [KeyboardKey.D5] = 0x17, [KeyboardKey.D6] = 0x16,
        [KeyboardKey.D7] = 0x1A, [KeyboardKey.D8] = 0x1C, [KeyboardKey.D9] = 0x19,
        [KeyboardKey.D0] = 0x1D,
        [KeyboardKey.F1] = 0x7A, [KeyboardKey.F2] = 0x78, [KeyboardKey.F3] = 0x63,
        [KeyboardKey.F4] = 0x76, [KeyboardKey.F5] = 0x60, [KeyboardKey.F6] = 0x61,
        [KeyboardKey.F7] = 0x62, [KeyboardKey.F8] = 0x64, [KeyboardKey.F9] = 0x65,
        [KeyboardKey.F10] = 0x6D, [KeyboardKey.F11] = 0x67, [KeyboardKey.F12] = 0x6F,
        [KeyboardKey.Escape] = 0x35, [KeyboardKey.Enter] = 0x24, [KeyboardKey.Tab] = 0x30,
        [KeyboardKey.Space] = 0x31, [KeyboardKey.Backspace] = 0x33, [KeyboardKey.CapsLock] = 0x39,
        [KeyboardKey.LeftShift] = 0x38, [KeyboardKey.RightShift] = 0x3C,
        [KeyboardKey.LeftCtrl] = 0x3B, [KeyboardKey.RightCtrl] = 0x3E,
        [KeyboardKey.LeftAlt] = 0x3A, [KeyboardKey.RightAlt] = 0x3D,
        [KeyboardKey.LeftMeta] = 0x37, [KeyboardKey.RightMeta] = 0x36,
        [KeyboardKey.Up] = 0x7E, [KeyboardKey.Down] = 0x7D,
        [KeyboardKey.Left] = 0x7B, [KeyboardKey.Right] = 0x7C,
        [KeyboardKey.Home] = 0x73, [KeyboardKey.End] = 0x77,
        [KeyboardKey.PageUp] = 0x74, [KeyboardKey.PageDown] = 0x79,
        [KeyboardKey.Insert] = 0x72, [KeyboardKey.Delete] = 0x75,
        [KeyboardKey.Minus] = 0x1B, [KeyboardKey.Equals] = 0x18,
        [KeyboardKey.LeftBracket] = 0x21, [KeyboardKey.RightBracket] = 0x1E,
        [KeyboardKey.Backslash] = 0x2A, [KeyboardKey.Semicolon] = 0x29,
        [KeyboardKey.Apostrophe] = 0x27, [KeyboardKey.Grave] = 0x32,
        [KeyboardKey.Comma] = 0x2B, [KeyboardKey.Period] = 0x2F, [KeyboardKey.Slash] = 0x2C,
        // macOS keyboards have no Print Screen/Scroll Lock/Pause; use F13-F15,
        // which occupy those positions on Apple extended keyboards.
        [KeyboardKey.PrintScreen] = 0x69, [KeyboardKey.ScrollLock] = 0x6B, [KeyboardKey.Pause] = 0x71,
        [KeyboardKey.VolumeUp] = 0x48, [KeyboardKey.VolumeDown] = 0x49, [KeyboardKey.VolumeMute] = 0x4A
    };

    public static bool TryGet(KeyboardKey key, out ushort code) => Codes.TryGetValue(key, out code);

    public static ulong ModifierFlag(KeyboardKey key) => key switch
    {
        KeyboardKey.LeftShift or KeyboardKey.RightShift => ShiftFlag,
        KeyboardKey.LeftCtrl or KeyboardKey.RightCtrl => ControlFlag,
        KeyboardKey.LeftAlt or KeyboardKey.RightAlt => AlternateFlag,
        KeyboardKey.LeftMeta or KeyboardKey.RightMeta => CommandFlag,
        _ => 0
    };
}
