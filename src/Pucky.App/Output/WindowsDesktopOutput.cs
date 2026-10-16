using System.Runtime.InteropServices;
using Pucky.Core.Mapping;

namespace Pucky.App.Output;

public sealed class WindowsDesktopOutput : IDesktopOutput
{
    private const uint InputMouse = 0;
    private const uint InputKeyboard = 1;
    private const int WheelDelta = 120;

    private KeySet _held;
    private float _mouseXRemainder;
    private float _mouseYRemainder;
    private float _wheelRemainder;
    private float _horizontalWheelRemainder;

    public string Name => "Windows SendInput (mouse + keyboard)";
    public bool IsAvailable => true;

    public void Submit(DesktopState state)
    {
        var inputs = new List<Input>(4);

        _mouseXRemainder += state.MouseX;
        _mouseYRemainder += state.MouseY;
        var x = (int)MathF.Truncate(_mouseXRemainder);
        var y = (int)MathF.Truncate(_mouseYRemainder);
        _mouseXRemainder -= x;
        _mouseYRemainder -= y;
        if (x != 0 || y != 0)
        {
            // Relative motion reaches games that read raw input, unlike
            // SetCursorPos, which only moves the visible desktop cursor.
            inputs.Add(MouseInputFor(MouseEvent.Move, dx: x, dy: y));
        }

        var held = state.HeldKeys;
        DesktopKeyDiff.Apply(_held, held, (key, down) => AddKey(inputs, key, down));
        _held = held;

        // Scroll is accumulated so slow pad motion still produces wheel
        // events; partial notches are sent for smooth-scrolling apps.
        _wheelRemainder += state.ScrollY * WheelDelta;
        _horizontalWheelRemainder += state.ScrollX * WheelDelta;
        var wheel = (int)MathF.Truncate(_wheelRemainder);
        var horizontalWheel = (int)MathF.Truncate(_horizontalWheelRemainder);
        _wheelRemainder -= wheel;
        _horizontalWheelRemainder -= horizontalWheel;
        if (wheel != 0)
        {
            inputs.Add(MouseInputFor(MouseEvent.Wheel, data: (uint)wheel));
        }
        if (horizontalWheel != 0)
        {
            inputs.Add(MouseInputFor(MouseEvent.HorizontalWheel, data: (uint)horizontalWheel));
        }

        if (inputs.Count > 0)
        {
            _ = SendInput((uint)inputs.Count, inputs.ToArray(), Marshal.SizeOf<Input>());
        }
    }

    public void Dispose()
    {
        if (!_held.IsEmpty)
        {
            Submit(DesktopState.Idle);
        }
    }

    private static void AddKey(List<Input> inputs, KeyboardKey key, bool down)
    {
        switch (key)
        {
            case KeyboardKey.MouseLeft:
                inputs.Add(MouseInputFor(down ? MouseEvent.LeftDown : MouseEvent.LeftUp));
                return;
            case KeyboardKey.MouseRight:
                inputs.Add(MouseInputFor(down ? MouseEvent.RightDown : MouseEvent.RightUp));
                return;
            case KeyboardKey.MouseMiddle:
                inputs.Add(MouseInputFor(down ? MouseEvent.MiddleDown : MouseEvent.MiddleUp));
                return;
            case KeyboardKey.MouseBack:
                inputs.Add(MouseInputFor(down ? MouseEvent.XDown : MouseEvent.XUp, data: 1));
                return;
            case KeyboardKey.MouseForward:
                inputs.Add(MouseInputFor(down ? MouseEvent.XDown : MouseEvent.XUp, data: 2));
                return;
            case KeyboardKey.WheelUp:
                if (down) inputs.Add(MouseInputFor(MouseEvent.Wheel, data: WheelDelta));
                return;
            case KeyboardKey.WheelDown:
                if (down) inputs.Add(MouseInputFor(MouseEvent.Wheel, data: unchecked((uint)-WheelDelta)));
                return;
            case KeyboardKey.WheelRight:
                if (down) inputs.Add(MouseInputFor(MouseEvent.HorizontalWheel, data: WheelDelta));
                return;
            case KeyboardKey.WheelLeft:
                if (down) inputs.Add(MouseInputFor(MouseEvent.HorizontalWheel, data: unchecked((uint)-WheelDelta)));
                return;
        }

        if (!WindowsKeyCodes.TryGet(key, out var code))
        {
            return;
        }

        var flags = down ? KeyEvent.None : KeyEvent.KeyUp;
        if (code.Extended)
        {
            flags |= KeyEvent.ExtendedKey;
        }
        if (code.ScanCode != 0)
        {
            // Scan codes are what DirectInput/raw-input games read.
            flags |= KeyEvent.ScanCode;
        }
        inputs.Add(new Input
        {
            Type = InputKeyboard,
            Data = new InputUnion
            {
                Keyboard = new KeyboardInput
                {
                    VirtualKey = code.ScanCode == 0 ? code.VirtualKey : (ushort)0,
                    ScanCode = code.ScanCode,
                    Flags = flags
                }
            }
        });
    }

    private static Input MouseInputFor(MouseEvent flags, int dx = 0, int dy = 0, uint data = 0) => new()
    {
        Type = InputMouse,
        Data = new InputUnion
        {
            Mouse = new MouseInput
            {
                Dx = dx,
                Dy = dy,
                MouseData = data,
                Flags = flags
            }
        }
    };

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(
        uint inputCount,
        [In] Input[] inputs,
        int inputSize);

    [StructLayout(LayoutKind.Sequential)]
    private struct Input
    {
        public uint Type;
        public InputUnion Data;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)]
        public MouseInput Mouse;
        [FieldOffset(0)]
        public KeyboardInput Keyboard;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseInput
    {
        public int Dx;
        public int Dy;
        public uint MouseData;
        public MouseEvent Flags;
        public uint Time;
        public UIntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KeyboardInput
    {
        public ushort VirtualKey;
        public ushort ScanCode;
        public KeyEvent Flags;
        public uint Time;
        public UIntPtr ExtraInfo;
    }

    [Flags]
    private enum MouseEvent : uint
    {
        None = 0,
        Move = 0x0001,
        LeftDown = 0x0002,
        LeftUp = 0x0004,
        RightDown = 0x0008,
        RightUp = 0x0010,
        MiddleDown = 0x0020,
        MiddleUp = 0x0040,
        XDown = 0x0080,
        XUp = 0x0100,
        Wheel = 0x0800,
        HorizontalWheel = 0x1000
    }

    [Flags]
    private enum KeyEvent : uint
    {
        None = 0,
        ExtendedKey = 0x0001,
        KeyUp = 0x0002,
        ScanCode = 0x0008
    }
}

internal readonly record struct WindowsKeyCode(ushort VirtualKey, ushort ScanCode, bool Extended);

internal static class WindowsKeyCodes
{
    private static readonly Dictionary<KeyboardKey, WindowsKeyCode> Codes = Build();

    public static bool TryGet(KeyboardKey key, out WindowsKeyCode code) =>
        Codes.TryGetValue(key, out code);

    private static Dictionary<KeyboardKey, WindowsKeyCode> Build()
    {
        var codes = new Dictionary<KeyboardKey, WindowsKeyCode>();
        // Set-1 scan codes for letters in A..Z order.
        ushort[] letterScanCodes =
        [
            0x1E, 0x30, 0x2E, 0x20, 0x12, 0x21, 0x22, 0x23, 0x17, 0x24, 0x25, 0x26, 0x32,
            0x31, 0x18, 0x19, 0x10, 0x13, 0x1F, 0x14, 0x16, 0x2F, 0x11, 0x2D, 0x15, 0x2C
        ];
        for (var index = 0; index < 26; index++)
        {
            codes[(KeyboardKey)((int)KeyboardKey.A + index)] = new((ushort)('A' + index), letterScanCodes[index], false);
        }
        for (var digit = 0; digit <= 9; digit++)
        {
            var scan = digit == 0 ? (ushort)0x0B : (ushort)(0x01 + digit);
            codes[(KeyboardKey)((int)KeyboardKey.D0 + digit)] = new((ushort)('0' + digit), scan, false);
        }
        ushort[] functionScanCodes = [0x3B, 0x3C, 0x3D, 0x3E, 0x3F, 0x40, 0x41, 0x42, 0x43, 0x44, 0x57, 0x58];
        for (var index = 0; index < 12; index++)
        {
            codes[(KeyboardKey)((int)KeyboardKey.F1 + index)] = new((ushort)(0x70 + index), functionScanCodes[index], false);
        }

        void Add(KeyboardKey key, ushort vk, ushort scan, bool extended = false) =>
            codes[key] = new(vk, scan, extended);

        Add(KeyboardKey.Escape, 0x1B, 0x01);
        Add(KeyboardKey.Enter, 0x0D, 0x1C);
        Add(KeyboardKey.Tab, 0x09, 0x0F);
        Add(KeyboardKey.Space, 0x20, 0x39);
        Add(KeyboardKey.Backspace, 0x08, 0x0E);
        Add(KeyboardKey.CapsLock, 0x14, 0x3A);
        Add(KeyboardKey.LeftShift, 0xA0, 0x2A);
        Add(KeyboardKey.RightShift, 0xA1, 0x36);
        Add(KeyboardKey.LeftCtrl, 0xA2, 0x1D);
        Add(KeyboardKey.RightCtrl, 0xA3, 0x1D, true);
        Add(KeyboardKey.LeftAlt, 0xA4, 0x38);
        Add(KeyboardKey.RightAlt, 0xA5, 0x38, true);
        Add(KeyboardKey.LeftMeta, 0x5B, 0x5B, true);
        Add(KeyboardKey.RightMeta, 0x5C, 0x5C, true);
        Add(KeyboardKey.Up, 0x26, 0x48, true);
        Add(KeyboardKey.Down, 0x28, 0x50, true);
        Add(KeyboardKey.Left, 0x25, 0x4B, true);
        Add(KeyboardKey.Right, 0x27, 0x4D, true);
        Add(KeyboardKey.Home, 0x24, 0x47, true);
        Add(KeyboardKey.End, 0x23, 0x4F, true);
        Add(KeyboardKey.PageUp, 0x21, 0x49, true);
        Add(KeyboardKey.PageDown, 0x22, 0x51, true);
        Add(KeyboardKey.Insert, 0x2D, 0x52, true);
        Add(KeyboardKey.Delete, 0x2E, 0x53, true);
        Add(KeyboardKey.Minus, 0xBD, 0x0C);
        Add(KeyboardKey.Equals, 0xBB, 0x0D);
        Add(KeyboardKey.LeftBracket, 0xDB, 0x1A);
        Add(KeyboardKey.RightBracket, 0xDD, 0x1B);
        Add(KeyboardKey.Backslash, 0xDC, 0x2B);
        Add(KeyboardKey.Semicolon, 0xBA, 0x27);
        Add(KeyboardKey.Apostrophe, 0xDE, 0x28);
        Add(KeyboardKey.Grave, 0xC0, 0x29);
        Add(KeyboardKey.Comma, 0xBC, 0x33);
        Add(KeyboardKey.Period, 0xBE, 0x34);
        Add(KeyboardKey.Slash, 0xBF, 0x35);
        Add(KeyboardKey.PrintScreen, 0x2C, 0x37, true);
        Add(KeyboardKey.ScrollLock, 0x91, 0x46);
        // Pause and media keys have no single scan code; send virtual keys.
        Add(KeyboardKey.Pause, 0x13, 0);
        Add(KeyboardKey.VolumeMute, 0xAD, 0, true);
        Add(KeyboardKey.VolumeDown, 0xAE, 0, true);
        Add(KeyboardKey.VolumeUp, 0xAF, 0, true);
        Add(KeyboardKey.MediaNext, 0xB0, 0, true);
        Add(KeyboardKey.MediaPrevious, 0xB1, 0, true);
        Add(KeyboardKey.MediaStop, 0xB2, 0, true);
        Add(KeyboardKey.MediaPlayPause, 0xB3, 0, true);
        return codes;
    }
}
