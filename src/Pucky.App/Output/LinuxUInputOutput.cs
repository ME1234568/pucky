using System.Runtime.InteropServices;
using Pucky.Core.Mapping;
using Microsoft.Win32.SafeHandles;

namespace Pucky.App.Output;

public sealed class LinuxUInputOutput(
    ILogger<LinuxUInputOutput> logger) : IVirtualOutput
{
    private const uint UiSetEvBit = 0x40045564;
    private const uint UiSetKeyBit = 0x40045565;
    private const uint UiSetAbsBit = 0x40045567;
    private const uint UiDevSetup = 0x405C5503;
    private const uint UiAbsSetup = 0x401C5504;
    private const uint UiDevCreate = 0x00005501;
    private const uint UiDevDestroy = 0x00005502;

    private const ushort EvSyn = 0x00;
    private const ushort EvKey = 0x01;
    private const ushort EvAbs = 0x03;
    private const ushort SynReport = 0;
    private const ushort BusUsb = 0x03;
    private static readonly ushort[] ButtonCodes =
    [
        304, 305, 307, 308, 310, 311, 314, 315, 316, 317, 318,
        544, 545, 546, 547
    ];
    private static readonly ushort[] AxisCodes = [0, 1, 2, 3, 4, 5];

    private FileStream? _stream;
    private VirtualGamepadState? _last;

    public string Name => "Xbox-compatible gamepad (uinput)";
    public bool IsConnected => _stream is not null;
    public string? UnavailableReason { get; private set; }
    public event Action<OutputFeedback>? FeedbackReceived
    {
        add { }
        remove { }
    }

    public bool Connect()
    {
        if (_stream is not null)
        {
            return true;
        }

        try
        {
            _stream = new FileStream(
                "/dev/uinput",
                FileMode.Open,
                FileAccess.ReadWrite,
                FileShare.ReadWrite);
            var handle = _stream.SafeFileHandle;
            Ioctl(handle, UiSetEvBit, EvKey);
            Ioctl(handle, UiSetEvBit, EvAbs);
            foreach (var button in ButtonCodes) Ioctl(handle, UiSetKeyBit, button);
            foreach (var axis in AxisCodes)
            {
                Ioctl(handle, UiSetAbsBit, axis);
                SetupAxis(handle, axis, axis is 2 or 5 ? 0 : -32768, 32767);
            }

            var setup = new UInputSetup
            {
                Id = new InputId
                {
                    BusType = BusUsb,
                    Vendor = 0x045E,
                    Product = 0x028E,
                    Version = 0x0114
                },
                Name = "Pucky Virtual Xbox Controller",
                FFEffectsMax = 0
            };
            IoctlStruct(handle, UiDevSetup, ref setup);
            Ioctl(handle, UiDevCreate, 0);
            UnavailableReason = null;
            return true;
        }
        catch (Exception ex)
        {
            UnavailableReason =
                "Cannot open /dev/uinput. Add your user to the input group and install the supplied udev rule.";
            logger.LogWarning(ex, "Could not create uinput controller");
            Disconnect();
            return false;
        }
    }

    public void Submit(VirtualGamepadState state)
    {
        if (_stream is null)
        {
            return;
        }

        WriteKey(304, state.Buttons.HasFlag(VirtualButton.A));
        WriteKey(305, state.Buttons.HasFlag(VirtualButton.B));
        // Linux names these positionally: BTN_NORTH is 307 and BTN_WEST is 308.
        WriteKey(307, state.Buttons.HasFlag(VirtualButton.Y));
        WriteKey(308, state.Buttons.HasFlag(VirtualButton.X));
        WriteKey(310, state.Buttons.HasFlag(VirtualButton.LeftBumper));
        WriteKey(311, state.Buttons.HasFlag(VirtualButton.RightBumper));
        WriteKey(314, state.Buttons.HasFlag(VirtualButton.Back));
        WriteKey(315, state.Buttons.HasFlag(VirtualButton.Start));
        WriteKey(316, state.Buttons.HasFlag(VirtualButton.Guide));
        WriteKey(317, state.Buttons.HasFlag(VirtualButton.LeftStick));
        WriteKey(318, state.Buttons.HasFlag(VirtualButton.RightStick));
        WriteKey(544, state.Buttons.HasFlag(VirtualButton.DPadUp));
        WriteKey(545, state.Buttons.HasFlag(VirtualButton.DPadDown));
        WriteKey(546, state.Buttons.HasFlag(VirtualButton.DPadLeft));
        WriteKey(547, state.Buttons.HasFlag(VirtualButton.DPadRight));
        WriteEvent(EvAbs, 0, ToAxis(state.LeftStick.X));
        WriteEvent(EvAbs, 1, ToAxis(-state.LeftStick.Y));
        WriteEvent(EvAbs, 3, ToAxis(state.RightStick.X));
        WriteEvent(EvAbs, 4, ToAxis(-state.RightStick.Y));
        WriteEvent(EvAbs, 2, ToTrigger(state.LeftTrigger));
        WriteEvent(EvAbs, 5, ToTrigger(state.RightTrigger));
        WriteEvent(EvSyn, SynReport, 0);
        _last = state;
    }

    public void Disconnect()
    {
        var stream = _stream;
        _stream = null;
        _last = null;
        if (stream is null)
        {
            return;
        }

        try
        {
            Ioctl(stream.SafeFileHandle, UiDevDestroy, 0);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "uinput destroy failed");
        }
        stream.Dispose();
    }

    public void Dispose() => Disconnect();

    private void WriteKey(ushort code, bool pressed)
    {
        var wasPressed = _last?.Buttons.HasFlag(ToVirtualButton(code)) ?? false;
        if (wasPressed != pressed)
        {
            WriteEvent(EvKey, code, pressed ? 1 : 0);
        }
    }

    private void WriteEvent(ushort type, ushort code, int value)
    {
        if (_stream is null) return;
        var bytes = new byte[24];
        BitConverter.TryWriteBytes(bytes.AsSpan(16, 2), type);
        BitConverter.TryWriteBytes(bytes.AsSpan(18, 2), code);
        BitConverter.TryWriteBytes(bytes.AsSpan(20, 4), value);
        if (write(_stream.SafeFileHandle, bytes, (nuint)bytes.Length) != bytes.Length)
        {
            throw new IOException(
                $"uinput event write failed: {Marshal.GetLastPInvokeError()}");
        }
    }

    private static void SetupAxis(SafeFileHandle handle, ushort code, int min, int max)
    {
        var setup = new UInputAbsSetup
        {
            Code = code,
            AbsInfo = new InputAbsInfo { Minimum = min, Maximum = max }
        };
        IoctlStruct(handle, UiAbsSetup, ref setup);
    }

    private static int ToAxis(float value) =>
        value < 0
            ? (int)Math.Round(Math.Clamp(value, -1, 1) * 32768)
            : (int)Math.Round(Math.Clamp(value, -1, 1) * 32767);

    private static int ToTrigger(float value) =>
        (int)Math.Round(Math.Clamp(value, 0, 1) * 32767);

    private static VirtualButton ToVirtualButton(ushort code) => code switch
    {
        304 => VirtualButton.A,
        305 => VirtualButton.B,
        307 => VirtualButton.Y,
        308 => VirtualButton.X,
        310 => VirtualButton.LeftBumper,
        311 => VirtualButton.RightBumper,
        314 => VirtualButton.Back,
        315 => VirtualButton.Start,
        316 => VirtualButton.Guide,
        317 => VirtualButton.LeftStick,
        318 => VirtualButton.RightStick,
        544 => VirtualButton.DPadUp,
        545 => VirtualButton.DPadDown,
        546 => VirtualButton.DPadLeft,
        547 => VirtualButton.DPadRight,
        _ => VirtualButton.None
    };

    private static void Ioctl(SafeFileHandle handle, uint request, int value)
    {
        if (ioctl(handle, request, value) < 0)
        {
            throw new IOException($"uinput ioctl 0x{request:X} failed: {Marshal.GetLastPInvokeError()}");
        }
    }

    private static void IoctlStruct<T>(SafeFileHandle handle, uint request, ref T value)
        where T : struct
    {
        var pointer = Marshal.AllocHGlobal(Marshal.SizeOf<T>());
        try
        {
            Marshal.StructureToPtr(value, pointer, false);
            if (ioctl_ptr(handle, request, pointer) < 0)
            {
                throw new IOException(
                    $"uinput ioctl 0x{request:X} failed: {Marshal.GetLastPInvokeError()}");
            }
        }
        finally
        {
            Marshal.FreeHGlobal(pointer);
        }
    }

    [DllImport("libc", EntryPoint = "ioctl", SetLastError = true)]
    private static extern int ioctl(SafeFileHandle fd, uint request, int value);

    [DllImport("libc", EntryPoint = "ioctl", SetLastError = true)]
    private static extern int ioctl_ptr(SafeFileHandle fd, uint request, IntPtr value);

    [DllImport("libc", SetLastError = true)]
    private static extern nint write(
        SafeFileHandle fd,
        [In] byte[] buffer,
        nuint count);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
    private struct UInputSetup
    {
        public InputId Id;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]
        public string Name;
        public uint FFEffectsMax;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct InputId
    {
        public ushort BusType;
        public ushort Vendor;
        public ushort Product;
        public ushort Version;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct UInputAbsSetup
    {
        public ushort Code;
        public ushort Padding;
        public InputAbsInfo AbsInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct InputAbsInfo
    {
        public int Value;
        public int Minimum;
        public int Maximum;
        public int Fuzz;
        public int Flat;
        public int Resolution;
    }
}
