using System.Runtime.InteropServices;
using Pucky.Core.Mapping;

namespace Pucky.App.Output;

public sealed class WindowsDesktopOutput : IDesktopOutput
{
    private bool _leftDown;
    private bool _rightDown;

    public string Name => "Windows SendInput";
    public bool IsAvailable => true;

    public void Submit(DesktopState state)
    {
        var flags = MouseEvent.Move;
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

        var input = new Input
        {
            Type = 0,
            Data = new InputUnion
            {
                Mouse = new MouseInput
                {
                    Dx = (int)Math.Round(state.MouseX),
                    Dy = (int)Math.Round(state.MouseY),
                    Flags = flags
                }
            }
        };
        _ = SendInput(1, [input], Marshal.SizeOf<Input>());

        if (Math.Abs(state.ScrollY) >= 0.01f)
        {
            input.Data.Mouse = new MouseInput
            {
                MouseData = (uint)(int)Math.Round(state.ScrollY * 120),
                Flags = MouseEvent.Wheel
            };
            _ = SendInput(1, [input], Marshal.SizeOf<Input>());
        }
    }

    public void Dispose()
    {
        if (_leftDown || _rightDown)
        {
            Submit(new DesktopState(0, 0, 0, 0, false, false));
        }
    }

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

    [Flags]
    private enum MouseEvent : uint
    {
        Move = 0x0001,
        LeftDown = 0x0002,
        LeftUp = 0x0004,
        RightDown = 0x0008,
        RightUp = 0x0010,
        Wheel = 0x0800
    }
}
