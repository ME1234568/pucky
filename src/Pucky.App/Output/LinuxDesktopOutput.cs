using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using Pucky.Core.Mapping;

namespace Pucky.App.Output;

internal sealed class LinuxDesktopOutput : IDesktopOutput
{
    private const uint UiSetEvBit = 0x40045564;
    private const uint UiSetKeyBit = 0x40045565;
    private const uint UiSetRelBit = 0x40045566;
    private const uint UiDevSetup = 0x405C5503;
    private const uint UiDevCreate = 0x5501;
    private const uint UiDevDestroy = 0x5502;

    private const ushort EvSyn = 0;
    private const ushort EvKey = 1;
    private const ushort EvRel = 2;
    private const ushort SynReport = 0;
    private const ushort RelX = 0;
    private const ushort RelY = 1;
    private const ushort RelHWheel = 6;
    private const ushort RelWheel = 8;
    private const ushort BtnLeft = 272;
    private const ushort BtnRight = 273;

    private FileStream? _stream;
    private bool _leftDown;
    private bool _rightDown;
    private float _mouseXRemainder;
    private float _mouseYRemainder;
    private float _wheelRemainder;
    private float _horizontalWheelRemainder;

    public bool IsAvailable => _stream is not null;
    public string Name => IsAvailable ? "uinput pointer" : "uinput pointer unavailable";

    public LinuxDesktopOutput()
    {
        try
        {
            _stream = new FileStream("/dev/uinput", FileMode.Open, FileAccess.Write, FileShare.ReadWrite);
            var handle = _stream.SafeFileHandle.DangerousGetHandle().ToInt32();

            Enable(handle, UiSetEvBit, EvKey);
            Enable(handle, UiSetEvBit, EvRel);
            Enable(handle, UiSetKeyBit, BtnLeft);
            Enable(handle, UiSetKeyBit, BtnRight);
            Enable(handle, UiSetRelBit, RelX);
            Enable(handle, UiSetRelBit, RelY);
            Enable(handle, UiSetRelBit, RelWheel);
            Enable(handle, UiSetRelBit, RelHWheel);

            var setup = new UInputSetup
            {
                Id = new InputId { BusType = 0x03, Vendor = 0x28DE, Product = 0x1302, Version = 1 },
                Name = "Pucky Virtual Pointer"
            };

            if (ioctl_setup(handle, UiDevSetup, ref setup) < 0 || ioctl(handle, UiDevCreate, 0) < 0)
            {
                throw new IOException("Could not create the uinput pointer.");
            }
        }
        catch
        {
            _stream?.Dispose();
            _stream = null;
        }
    }

    public void Submit(DesktopState state)
    {
        if (_stream is null)
        {
            return;
        }

        _mouseXRemainder += state.MouseX;
        _mouseYRemainder += state.MouseY;
        var x = (int)MathF.Truncate(_mouseXRemainder);
        var y = (int)MathF.Truncate(_mouseYRemainder);
        _mouseXRemainder -= x;
        _mouseYRemainder -= y;
        _wheelRemainder += state.ScrollY;
        _horizontalWheelRemainder += state.ScrollX;
        var wheel = (int)MathF.Truncate(_wheelRemainder);
        var horizontalWheel = (int)MathF.Truncate(_horizontalWheelRemainder);
        _wheelRemainder -= wheel;
        _horizontalWheelRemainder -= horizontalWheel;

        if (x != 0) WriteEvent(EvRel, RelX, x);
        if (y != 0) WriteEvent(EvRel, RelY, y);
        if (wheel != 0) WriteEvent(EvRel, RelWheel, wheel);
        if (horizontalWheel != 0) WriteEvent(EvRel, RelHWheel, horizontalWheel);
        WriteButton(BtnLeft, state.LeftClick, ref _leftDown);
        WriteButton(BtnRight, state.RightClick, ref _rightDown);
        WriteEvent(EvSyn, SynReport, 0);
    }

    private void WriteButton(ushort code, bool down, ref bool previous)
    {
        if (down == previous)
        {
            return;
        }

        previous = down;
        WriteEvent(EvKey, code, down ? 1 : 0);
    }

    private void WriteEvent(ushort type, ushort code, int value)
    {
        var buffer = new byte[24];
        BitConverter.TryWriteBytes(buffer.AsSpan(16, 2), type);
        BitConverter.TryWriteBytes(buffer.AsSpan(18, 2), code);
        BitConverter.TryWriteBytes(buffer.AsSpan(20, 4), value);
        if (write(_stream!.SafeFileHandle, buffer, (nuint)buffer.Length) != buffer.Length)
        {
            throw new IOException(
                $"uinput event write failed: {Marshal.GetLastPInvokeError()}");
        }
    }

    private static void Enable(int handle, uint request, ushort value)
    {
        if (ioctl(handle, request, value) < 0)
        {
            throw new IOException($"uinput ioctl {request:X} failed.");
        }
    }

    public void Dispose()
    {
        if (_stream is null)
        {
            return;
        }

        var handle = _stream.SafeFileHandle.DangerousGetHandle().ToInt32();
        ioctl(handle, UiDevDestroy, 0);
        _stream.Dispose();
        _stream = null;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
    private struct UInputSetup
    {
        public InputId Id;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]
        public string Name;
        public uint FfEffectsMax;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct InputId
    {
        public ushort BusType;
        public ushort Vendor;
        public ushort Product;
        public ushort Version;
    }

    [DllImport("libc", SetLastError = true)]
    private static extern int ioctl(int fd, uint request, int value);

    [DllImport("libc", EntryPoint = "ioctl", SetLastError = true)]
    private static extern int ioctl_setup(int fd, uint request, ref UInputSetup value);

    [DllImport("libc", SetLastError = true)]
    private static extern nint write(
        SafeFileHandle fd,
        [In] byte[] buffer,
        nuint count);
}
