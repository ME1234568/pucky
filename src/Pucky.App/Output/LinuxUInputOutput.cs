using System.Buffers.Binary;
using System.Runtime.InteropServices;
using Pucky.Core.Mapping;
using Microsoft.Win32.SafeHandles;

namespace Pucky.App.Output;

/// <summary>
/// An Xbox-compatible uinput gamepad. It advertises FF_RUMBLE so games can
/// upload and play rumble effects, which are forwarded to the controller.
/// </summary>
public sealed class LinuxUInputOutput(
    ILogger<LinuxUInputOutput> logger) : IVirtualOutput
{
    private const uint UiSetEvBit = 0x40045564;
    private const uint UiSetKeyBit = 0x40045565;
    private const uint UiSetAbsBit = 0x40045567;
    private const uint UiSetFfBit = 0x4004556B;
    private const uint UiDevSetup = 0x405C5503;
    private const uint UiAbsSetup = 0x401C5504;
    private const uint UiDevCreate = 0x00005501;
    private const uint UiDevDestroy = 0x00005502;
    // _IOWR/_IOW('U', 200..203) with sizeof(uinput_ff_upload) == 104 and
    // sizeof(uinput_ff_erase) == 12 on 64-bit Linux.
    private const uint UiBeginFfUpload = 0xC06855C8;
    private const uint UiEndFfUpload = 0x406855C9;
    private const uint UiBeginFfErase = 0xC00C55CA;
    private const uint UiEndFfErase = 0x400C55CB;

    private const ushort EvSyn = 0x00;
    private const ushort EvKey = 0x01;
    private const ushort EvAbs = 0x03;
    private const ushort EvFf = 0x15;
    private const ushort EvUInput = 0x0101;
    private const ushort UiFfUpload = 1;
    private const ushort UiFfErase = 2;
    private const ushort FfRumble = 0x50;
    private const ushort FfGain = 0x60;
    private const int MaxEffects = 16;
    private const int UploadSize = 104;
    private const int EffectOffset = 8;
    private const int EraseSize = 12;
    private const short PollIn = 0x0001;
    private const ushort SynReport = 0;
    private const ushort BusUsb = 0x03;
    private const int InputEventSize = 24;
    private static readonly ushort[] ButtonCodes =
    [
        304, 305, 307, 308, 310, 311, 314, 315, 316, 317, 318,
        544, 545, 546, 547
    ];
    private static readonly ushort[] AxisCodes = [0, 1, 2, 3, 4, 5];

    private readonly byte[] _event = new byte[InputEventSize];
    private readonly object _effectLock = new();
    private readonly RumbleEffect[] _effects = new RumbleEffect[MaxEffects];
    private FileStream? _stream;
    private VirtualGamepadState? _last;
    private Thread? _feedbackThread;
    private volatile bool _feedbackRunning;
    private OutputFeedback _lastFeedback = new(0, 0);
    private Timer? _effectTimer;

    public string Name => "Xbox-compatible gamepad (uinput)";
    public bool IsConnected => _stream is not null;
    public string? UnavailableReason { get; private set; }
    public event Action<OutputFeedback>? FeedbackReceived;

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
            Ioctl(handle, UiSetEvBit, EvFf);
            Ioctl(handle, UiSetFfBit, FfRumble);
            Ioctl(handle, UiSetFfBit, FfGain);
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
                FFEffectsMax = MaxEffects
            };
            IoctlStruct(handle, UiDevSetup, ref setup);
            Ioctl(handle, UiDevCreate, 0);
            UnavailableReason = null;
            StartFeedbackThread();
            return true;
        }
        catch (Exception ex)
        {
            UnavailableReason =
                "Cannot open /dev/uinput. Run packaging/linux/install-linux.sh (or install the udev rule), then log out and back in.";
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

        WriteKey(304, state.Buttons, VirtualButton.A);
        WriteKey(305, state.Buttons, VirtualButton.B);
        // Linux names these positionally: BTN_NORTH is 307 and BTN_WEST is 308.
        WriteKey(307, state.Buttons, VirtualButton.Y);
        WriteKey(308, state.Buttons, VirtualButton.X);
        WriteKey(310, state.Buttons, VirtualButton.LeftBumper);
        WriteKey(311, state.Buttons, VirtualButton.RightBumper);
        WriteKey(314, state.Buttons, VirtualButton.Back);
        WriteKey(315, state.Buttons, VirtualButton.Start);
        WriteKey(316, state.Buttons, VirtualButton.Guide);
        WriteKey(317, state.Buttons, VirtualButton.LeftStick);
        WriteKey(318, state.Buttons, VirtualButton.RightStick);
        WriteKey(544, state.Buttons, VirtualButton.DPadUp);
        WriteKey(545, state.Buttons, VirtualButton.DPadDown);
        WriteKey(546, state.Buttons, VirtualButton.DPadLeft);
        WriteKey(547, state.Buttons, VirtualButton.DPadRight);
        WriteAxis(0, ToAxis(state.LeftStick.X), _last is null ? null : ToAxis(_last.LeftStick.X));
        WriteAxis(1, ToAxis(-state.LeftStick.Y), _last is null ? null : ToAxis(-_last.LeftStick.Y));
        WriteAxis(3, ToAxis(state.RightStick.X), _last is null ? null : ToAxis(_last.RightStick.X));
        WriteAxis(4, ToAxis(-state.RightStick.Y), _last is null ? null : ToAxis(-_last.RightStick.Y));
        WriteAxis(2, ToTrigger(state.LeftTrigger), _last is null ? null : ToTrigger(_last.LeftTrigger));
        WriteAxis(5, ToTrigger(state.RightTrigger), _last is null ? null : ToTrigger(_last.RightTrigger));
        WriteEvent(EvSyn, SynReport, 0);
        _last = state;
    }

    public void Disconnect()
    {
        var stream = _stream;
        _stream = null;
        _last = null;
        _feedbackRunning = false;
        _feedbackThread?.Join(TimeSpan.FromMilliseconds(500));
        _feedbackThread = null;
        _effectTimer?.Dispose();
        _effectTimer = null;
        lock (_effectLock)
        {
            Array.Clear(_effects);
        }
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
        PublishFeedback(new OutputFeedback(0, 0));
    }

    public void Dispose() => Disconnect();

    private void StartFeedbackThread()
    {
        _feedbackRunning = true;
        _effectTimer = new Timer(_ => PublishActiveEffects(), null, Timeout.Infinite, Timeout.Infinite);
        _feedbackThread = new Thread(FeedbackLoop)
        {
            IsBackground = true,
            Name = "Pucky uinput force feedback"
        };
        _feedbackThread.Start();
    }

    /// <summary>
    /// Services the kernel's force-feedback protocol: effect uploads and
    /// erasures must be acknowledged, and EV_FF events start/stop effects.
    /// </summary>
    private void FeedbackLoop()
    {
        var buffer = new byte[InputEventSize];
        while (_feedbackRunning)
        {
            var stream = _stream;
            if (stream is null)
            {
                return;
            }

            try
            {
                var poll = new PollFd { Fd = stream.SafeFileHandle.DangerousGetHandle().ToInt32(), Events = PollIn };
                var ready = poll_fd(ref poll, 1, 100);
                if (ready <= 0 || (poll.ReturnedEvents & PollIn) == 0)
                {
                    continue;
                }

                var count = read(stream.SafeFileHandle, buffer, (nuint)buffer.Length);
                if (count != buffer.Length)
                {
                    continue;
                }

                var type = BinaryPrimitives.ReadUInt16LittleEndian(buffer.AsSpan(16));
                var code = BinaryPrimitives.ReadUInt16LittleEndian(buffer.AsSpan(18));
                var value = BinaryPrimitives.ReadInt32LittleEndian(buffer.AsSpan(20));
                if (type == EvUInput && code == UiFfUpload)
                {
                    HandleUpload(stream.SafeFileHandle, (uint)value);
                }
                else if (type == EvUInput && code == UiFfErase)
                {
                    HandleErase(stream.SafeFileHandle, (uint)value);
                }
                else if (type == EvFf && code < MaxEffects)
                {
                    lock (_effectLock)
                    {
                        _effects[code].PlayingUntil = value > 0
                            ? DateTime.UtcNow.AddMilliseconds(
                                _effects[code].LengthMilliseconds == 0
                                    ? int.MaxValue / 2
                                    : _effects[code].LengthMilliseconds)
                            : DateTime.MinValue;
                    }
                    PublishActiveEffects();
                }
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException)
            {
                logger.LogDebug(ex, "uinput force-feedback loop stopped");
                return;
            }
        }
    }

    private void HandleUpload(SafeFileHandle handle, uint requestId)
    {
        var upload = new byte[UploadSize];
        BinaryPrimitives.WriteUInt32LittleEndian(upload, requestId);
        if (!IoctlBuffer(handle, UiBeginFfUpload, upload))
        {
            return;
        }

        // struct ff_effect: type, id, direction, trigger(4), replay(length,
        // delay), padding, then the union; ff_rumble_effect is
        // strong_magnitude, weak_magnitude.
        var effect = upload.AsSpan(EffectOffset);
        var type = BinaryPrimitives.ReadUInt16LittleEndian(effect);
        var id = BinaryPrimitives.ReadInt16LittleEndian(effect[2..]);
        var length = BinaryPrimitives.ReadUInt16LittleEndian(effect[10..]);
        var strong = BinaryPrimitives.ReadUInt16LittleEndian(effect[16..]);
        var weak = BinaryPrimitives.ReadUInt16LittleEndian(effect[18..]);
        var accepted = type == FfRumble && id is >= 0 and < MaxEffects;
        if (accepted)
        {
            lock (_effectLock)
            {
                var playingUntil = _effects[id].PlayingUntil;
                _effects[id] = new RumbleEffect(strong, weak, length) { PlayingUntil = playingUntil };
            }
            PublishActiveEffects();
        }

        // retval: 0 accepts the effect, -EINVAL (22) rejects unsupported types.
        BinaryPrimitives.WriteInt32LittleEndian(upload.AsSpan(4), accepted ? 0 : -22);
        IoctlBuffer(handle, UiEndFfUpload, upload);
    }

    private void HandleErase(SafeFileHandle handle, uint requestId)
    {
        var erase = new byte[EraseSize];
        BinaryPrimitives.WriteUInt32LittleEndian(erase, requestId);
        if (!IoctlBuffer(handle, UiBeginFfErase, erase))
        {
            return;
        }

        var id = BinaryPrimitives.ReadUInt32LittleEndian(erase.AsSpan(8));
        if (id < MaxEffects)
        {
            lock (_effectLock)
            {
                _effects[id] = default;
            }
            PublishActiveEffects();
        }
        BinaryPrimitives.WriteInt32LittleEndian(erase.AsSpan(4), 0);
        IoctlBuffer(handle, UiEndFfErase, erase);
    }

    /// <summary>Combines playing effects and schedules the next expiry.</summary>
    private void PublishActiveEffects()
    {
        var now = DateTime.UtcNow;
        var strong = 0;
        var weak = 0;
        var nextExpiry = DateTime.MaxValue;
        lock (_effectLock)
        {
            foreach (var effect in _effects)
            {
                if (effect.PlayingUntil <= now)
                {
                    continue;
                }
                strong = Math.Max(strong, effect.Strong);
                weak = Math.Max(weak, effect.Weak);
                if (effect.PlayingUntil < nextExpiry)
                {
                    nextExpiry = effect.PlayingUntil;
                }
            }
        }

        if (nextExpiry != DateTime.MaxValue)
        {
            var due = Math.Clamp((nextExpiry - now).TotalMilliseconds + 1, 1, int.MaxValue / 2);
            _effectTimer?.Change((long)due, Timeout.Infinite);
        }

        PublishFeedback(new OutputFeedback((byte)(strong >> 8), (byte)(weak >> 8)));
    }

    private void PublishFeedback(OutputFeedback feedback)
    {
        if (feedback == _lastFeedback)
        {
            return;
        }
        _lastFeedback = feedback;
        FeedbackReceived?.Invoke(feedback);
    }

    private void WriteKey(ushort code, VirtualButton buttons, VirtualButton button)
    {
        var pressed = buttons.HasFlag(button);
        var wasPressed = _last?.Buttons.HasFlag(button) ?? false;
        if (_last is null || wasPressed != pressed)
        {
            WriteEvent(EvKey, code, pressed ? 1 : 0);
        }
    }

    private void WriteAxis(ushort code, int value, int? previous)
    {
        if (previous != value)
        {
            WriteEvent(EvAbs, code, value);
        }
    }

    private void WriteEvent(ushort type, ushort code, int value)
    {
        var stream = _stream;
        if (stream is null) return;
        Array.Clear(_event);
        BitConverter.TryWriteBytes(_event.AsSpan(16, 2), type);
        BitConverter.TryWriteBytes(_event.AsSpan(18, 2), code);
        BitConverter.TryWriteBytes(_event.AsSpan(20, 4), value);
        if (write(stream.SafeFileHandle, _event, (nuint)_event.Length) != _event.Length)
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

    private static void Ioctl(SafeFileHandle handle, uint request, int value)
    {
        if (ioctl(handle, request, value) < 0)
        {
            throw new IOException($"uinput ioctl 0x{request:X} failed: {Marshal.GetLastPInvokeError()}");
        }
    }

    private bool IoctlBuffer(SafeFileHandle handle, uint request, byte[] buffer)
    {
        if (ioctl_buffer(handle, request, buffer) < 0)
        {
            logger.LogDebug(
                "uinput force-feedback ioctl 0x{Request:X} failed: {Error}",
                request,
                Marshal.GetLastPInvokeError());
            return false;
        }
        return true;
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

    private struct RumbleEffect(ushort strong, ushort weak, ushort lengthMilliseconds)
    {
        public ushort Strong = strong;
        public ushort Weak = weak;
        public ushort LengthMilliseconds = lengthMilliseconds;
        public DateTime PlayingUntil = DateTime.MinValue;
    }

    [DllImport("libc", EntryPoint = "ioctl", SetLastError = true)]
    private static extern int ioctl(SafeFileHandle fd, uint request, int value);

    [DllImport("libc", EntryPoint = "ioctl", SetLastError = true)]
    private static extern int ioctl_ptr(SafeFileHandle fd, uint request, IntPtr value);

    [DllImport("libc", EntryPoint = "ioctl", SetLastError = true)]
    private static extern int ioctl_buffer(SafeFileHandle fd, uint request, [In, Out] byte[] value);

    [DllImport("libc", SetLastError = true)]
    private static extern nint write(
        SafeFileHandle fd,
        [In] byte[] buffer,
        nuint count);

    [DllImport("libc", SetLastError = true)]
    private static extern nint read(
        SafeFileHandle fd,
        [Out] byte[] buffer,
        nuint count);

    [DllImport("libc", EntryPoint = "poll", SetLastError = true)]
    private static extern int poll_fd(ref PollFd fds, nuint count, int timeoutMilliseconds);

    [StructLayout(LayoutKind.Sequential)]
    private struct PollFd
    {
        public int Fd;
        public short Events;
        public short ReturnedEvents;
    }

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
