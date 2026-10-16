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

    private readonly byte[] _event = new byte[24];
    private FileStream? _stream;
    private KeySet _held;
    private bool _pending;
    private float _mouseXRemainder;
    private float _mouseYRemainder;
    private float _wheelRemainder;
    private float _horizontalWheelRemainder;

    public bool IsAvailable => _stream is not null;
    public string Name => IsAvailable ? "uinput pointer + keyboard" : "uinput pointer unavailable";

    public LinuxDesktopOutput()
    {
        try
        {
            _stream = new FileStream("/dev/uinput", FileMode.Open, FileAccess.Write, FileShare.ReadWrite);
            var handle = _stream.SafeFileHandle.DangerousGetHandle().ToInt32();

            Enable(handle, UiSetEvBit, EvKey);
            Enable(handle, UiSetEvBit, EvRel);
            foreach (var code in LinuxKeyCodes.All)
            {
                Enable(handle, UiSetKeyBit, code);
            }
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

        var held = state.HeldKeys;
        DesktopKeyDiff.Apply(_held, held, Emit);
        _held = held;

        if (_pending)
        {
            WriteEvent(EvSyn, SynReport, 0);
            _pending = false;
        }
    }

    private void Emit(KeyboardKey key, bool down)
    {
        switch (key)
        {
            case KeyboardKey.WheelUp:
                if (down) WriteEvent(EvRel, RelWheel, 1);
                return;
            case KeyboardKey.WheelDown:
                if (down) WriteEvent(EvRel, RelWheel, -1);
                return;
            case KeyboardKey.WheelRight:
                if (down) WriteEvent(EvRel, RelHWheel, 1);
                return;
            case KeyboardKey.WheelLeft:
                if (down) WriteEvent(EvRel, RelHWheel, -1);
                return;
        }

        if (LinuxKeyCodes.TryGet(key, out var code))
        {
            WriteEvent(EvKey, code, down ? 1 : 0);
        }
    }

    private void WriteEvent(ushort type, ushort code, int value)
    {
        Array.Clear(_event);
        BitConverter.TryWriteBytes(_event.AsSpan(16, 2), type);
        BitConverter.TryWriteBytes(_event.AsSpan(18, 2), code);
        BitConverter.TryWriteBytes(_event.AsSpan(20, 4), value);
        if (write(_stream!.SafeFileHandle, _event, (nuint)_event.Length) != _event.Length)
        {
            throw new IOException(
                $"uinput event write failed: {Marshal.GetLastPInvokeError()}");
        }
        if (type != EvSyn)
        {
            _pending = true;
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

        try
        {
            if (!_held.IsEmpty)
            {
                Submit(DesktopState.Idle);
            }
        }
        catch (IOException)
        {
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

/// <summary>Linux input-event-codes.h key and button codes.</summary>
internal static class LinuxKeyCodes
{
    private static readonly Dictionary<KeyboardKey, ushort> Codes = Build();

    public static IEnumerable<ushort> All => Codes.Values;

    public static bool TryGet(KeyboardKey key, out ushort code) => Codes.TryGetValue(key, out code);

    private static Dictionary<KeyboardKey, ushort> Build()
    {
        // Letters in A..Z order.
        ushort[] letters =
        [
            30, 48, 46, 32, 18, 33, 34, 35, 23, 36, 37, 38, 50,
            49, 24, 25, 16, 19, 31, 20, 22, 47, 17, 45, 21, 44
        ];
        var codes = new Dictionary<KeyboardKey, ushort>();
        for (var index = 0; index < 26; index++)
        {
            codes[(KeyboardKey)((int)KeyboardKey.A + index)] = letters[index];
        }
        for (var digit = 1; digit <= 9; digit++)
        {
            codes[(KeyboardKey)((int)KeyboardKey.D0 + digit)] = (ushort)(1 + digit);
        }
        codes[KeyboardKey.D0] = 11;
        for (var index = 0; index < 10; index++)
        {
            codes[(KeyboardKey)((int)KeyboardKey.F1 + index)] = (ushort)(59 + index);
        }
        codes[KeyboardKey.F11] = 87;
        codes[KeyboardKey.F12] = 88;

        codes[KeyboardKey.Escape] = 1;
        codes[KeyboardKey.Enter] = 28;
        codes[KeyboardKey.Tab] = 15;
        codes[KeyboardKey.Space] = 57;
        codes[KeyboardKey.Backspace] = 14;
        codes[KeyboardKey.CapsLock] = 58;
        codes[KeyboardKey.LeftShift] = 42;
        codes[KeyboardKey.RightShift] = 54;
        codes[KeyboardKey.LeftCtrl] = 29;
        codes[KeyboardKey.RightCtrl] = 97;
        codes[KeyboardKey.LeftAlt] = 56;
        codes[KeyboardKey.RightAlt] = 100;
        codes[KeyboardKey.LeftMeta] = 125;
        codes[KeyboardKey.RightMeta] = 126;
        codes[KeyboardKey.Up] = 103;
        codes[KeyboardKey.Down] = 108;
        codes[KeyboardKey.Left] = 105;
        codes[KeyboardKey.Right] = 106;
        codes[KeyboardKey.Home] = 102;
        codes[KeyboardKey.End] = 107;
        codes[KeyboardKey.PageUp] = 104;
        codes[KeyboardKey.PageDown] = 109;
        codes[KeyboardKey.Insert] = 110;
        codes[KeyboardKey.Delete] = 111;
        codes[KeyboardKey.Minus] = 12;
        codes[KeyboardKey.Equals] = 13;
        codes[KeyboardKey.LeftBracket] = 26;
        codes[KeyboardKey.RightBracket] = 27;
        codes[KeyboardKey.Backslash] = 43;
        codes[KeyboardKey.Semicolon] = 39;
        codes[KeyboardKey.Apostrophe] = 40;
        codes[KeyboardKey.Grave] = 41;
        codes[KeyboardKey.Comma] = 51;
        codes[KeyboardKey.Period] = 52;
        codes[KeyboardKey.Slash] = 53;
        codes[KeyboardKey.PrintScreen] = 99;
        codes[KeyboardKey.ScrollLock] = 70;
        codes[KeyboardKey.Pause] = 119;
        codes[KeyboardKey.VolumeMute] = 113;
        codes[KeyboardKey.VolumeDown] = 114;
        codes[KeyboardKey.VolumeUp] = 115;
        codes[KeyboardKey.MediaNext] = 163;
        codes[KeyboardKey.MediaPlayPause] = 164;
        codes[KeyboardKey.MediaPrevious] = 165;
        codes[KeyboardKey.MediaStop] = 166;
        codes[KeyboardKey.MouseLeft] = 272;
        codes[KeyboardKey.MouseRight] = 273;
        codes[KeyboardKey.MouseMiddle] = 274;
        codes[KeyboardKey.MouseBack] = 275;
        codes[KeyboardKey.MouseForward] = 276;
        return codes;
    }
}
