using System.Runtime.InteropServices;
using Pucky.Core.Mapping;

namespace Pucky.App.Output;

public sealed class WindowsDesktopOutput : IDesktopOutput
{
    private bool _leftDown;
    private bool _rightDown;
    private float _mouseXRemainder;
    private float _mouseYRemainder;

    public string Name => "Windows cursor + SendInput";
    public bool IsAvailable => true;

    public void Submit(DesktopState state)
    {
        var flags = MouseEvent.None;
        if (state.LeftClick != _leftDown)
        {
            flags |= state.LeftClick ? MouseEvent.LeftDown : MouseEvent.LeftUp;
            _leftDown = state.LeftClick;
        }
        if (state.RightClick != _rightDown)
        {
            flags |= state.RightClick ? MouseEvent.RightDown : MouseEvent.RightUp;
            _rightDown = state.RightClick;
        }

        _mouseXRemainder += state.MouseX;
        _mouseYRemainder += state.MouseY;
        var x = (int)MathF.Truncate(_mouseXRemainder);
        var y = (int)MathF.Truncate(_mouseYRemainder);
        _mouseXRemainder -= x;
        _mouseYRemainder -= y;

        if ((x != 0 || y != 0) &&
            GetCursorPos(out var cursor))
        {
            _ = SetCursorPos(cursor.X + x, cursor.Y + y);
        }

        if (flags != MouseEvent.None)
        {
            SendMouseInput(flags);
        }

        if (Math.Abs(state.ScrollY) >= 0.01f)
        {
            SendMouseInput(
                MouseEvent.Wheel,
                (uint)(int)Math.Round(state.ScrollY * 120));
        }
    }

    public void Dispose()
    {
        if (_leftDown || _rightDown)
        {
            Submit(new DesktopState(0, 0, 0, 0, false, false));
        }
    }

    private static void SendMouseInput(MouseEvent flags, uint mouseData = 0)
    {
        var input = new Input
        {
            Type = 0,
            Data = new InputUnion
            {
                Mouse = new MouseInput
                {
                    MouseData = mouseData,
                    Flags = flags
                }
            }
        };
        _ = SendInput(1, [input], Marshal.SizeOf<Input>());
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out Point point);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetCursorPos(int x, int y);

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
    private struct Point
    {
        public int X;
        public int Y;
    }

    [Flags]
    private enum MouseEvent : uint
    {
        None = 0,
        LeftDown = 0x0002,
        LeftUp = 0x0004,
        RightDown = 0x0008,
        RightUp = 0x0010,
        Wheel = 0x0800
    }
}
