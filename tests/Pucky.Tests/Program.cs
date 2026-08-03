using System.Buffers.Binary;
using Pucky.Core.Input;
using Pucky.Core.Mapping;

var tests = new (string Name, Action Run)[]
{
    ("parses a full Triton state report", ParseState),
    ("parses a timestamped Triton state report", ParseTimestampState),
    ("distinguishes View and Menu report bits", ParseViewAndMenu),
    ("rejects unrelated reports", RejectInvalidReport),
    ("parses battery status", ParseBattery),
    ("builds feature commands", BuildFeatureCommand),
    ("builds Triton vibration reports", BuildVibrationReport),
    ("builds Triton trackpad haptic pulses", BuildTrackpadHapticPulse),
    ("creates trackpad motion and click haptics", CreateTrackpadHaptics),
    ("scales trackpad haptics across the full range", ScaleTrackpadHaptics),
    ("applies radial deadzones", ApplyDeadzone),
    ("glides the cursor after a trackpad swipe", GlideTrackpadMouse),
    ("maps buttons and action layers", MapLayer),
    ("maps all four rear buttons", MapRearButtons),
    ("maps the trackpad to a D-pad", MapTrackpadDPad),
    ("encodes every macOS HID button usage", EncodeEveryMacHidButton),
    ("encodes a macOS HID gamepad report", EncodeMacHidGamepad)
};

static void EncodeEveryMacHidButton()
{
    var expectedMappings = new (VirtualButton Button, int Bit)[]
    {
        (VirtualButton.A, 0),
        (VirtualButton.B, 1),
        (VirtualButton.X, 3),
        (VirtualButton.Y, 4),
        (VirtualButton.LeftBumper, 6),
        (VirtualButton.RightBumper, 7),
        (VirtualButton.Back, 10),
        (VirtualButton.Start, 11),
        (VirtualButton.Guide, 12),
        (VirtualButton.LeftStick, 13),
        (VirtualButton.RightStick, 14),
        (VirtualButton.QuickAccess, 15)
    };

    foreach (var (button, bit) in expectedMappings)
    {
        var state = new VirtualGamepadState(button, Axis2.Zero, Axis2.Zero, 0, 0);
        var report = MacHidGamepadReport.Encode(state);
        var expectedLow = bit < 8 ? (byte)(1 << bit) : (byte)0;
        var expectedHigh = bit >= 8 ? (byte)(1 << (bit - 8)) : (byte)0;
        Equal(expectedLow, report[0]);
        Equal(expectedHigh, report[1]);
    }
}

static void EncodeMacHidGamepad()
{
    var state = new VirtualGamepadState(
        VirtualButton.A |
        VirtualButton.Y |
        VirtualButton.RightBumper |
        VirtualButton.Start |
        VirtualButton.Guide |
        VirtualButton.QuickAccess |
        VirtualButton.RightStick |
        VirtualButton.DPadUp |
        VirtualButton.DPadRight,
        new Axis2(-1, 1),
        new Axis2(0.5f, -0.5f),
        0,
        1);

    var report = MacHidGamepadReport.Encode(state);
    Equal(MacHidGamepadReport.Length, report.Length);
    Equal((byte)0x91, report[0]);
    Equal((byte)0xD8, report[1]);
    Equal((byte)1, report[2]);
    Equal(short.MinValue + 1, BinaryPrimitives.ReadInt16LittleEndian(report.AsSpan(3)));
    Equal(short.MinValue + 1, BinaryPrimitives.ReadInt16LittleEndian(report.AsSpan(5)));
    Equal((short)16384, BinaryPrimitives.ReadInt16LittleEndian(report.AsSpan(7)));
    Equal(short.MinValue, BinaryPrimitives.ReadInt16LittleEndian(report.AsSpan(9)));
    Equal(short.MaxValue, BinaryPrimitives.ReadInt16LittleEndian(report.AsSpan(11)));
    Equal((short)16384, BinaryPrimitives.ReadInt16LittleEndian(report.AsSpan(13)));
}

var failures = 0;
foreach (var (name, run) in tests)
{
    try
    {
        run();
        Console.WriteLine($"PASS {name}");
    }
    catch (Exception exception)
    {
        failures++;
        Console.Error.WriteLine($"FAIL {name}: {exception.Message}");
    }
}

Console.WriteLine($"{tests.Length - failures}/{tests.Length} tests passed");
return failures == 0 ? 0 : 1;

static void ParseState()
{
    var report = new byte[64];
    report[0] = SteamControllerProtocol.StateReport;
    report[1] = 42;
    report[2] = 0xF1; // A, QAM, RS, Menu, R4
    report[3] = 0x62; // RB, dpad up, View
    report[4] = 0x6B; // Steam, L4, LB, right pad touch/click
    report[5] = 0x23; // LS touch, left pad touch, left grip
    WriteInt16(report, 6, 16384);
    WriteInt16(report, 8, 32767);
    WriteInt16(report, 10, -32768);
    WriteInt16(report, 12, 32767);
    WriteInt16(report, 14, 8192);
    WriteInt16(report, 16, -8192);
    WriteInt16(report, 18, -16000);
    WriteInt16(report, 20, 12000);
    WriteUInt16(report, 22, 40000);
    WriteInt16(report, 24, 20000);
    WriteInt16(report, 26, -20000);
    WriteUInt16(report, 28, 50000);
    BinaryPrimitives.WriteUInt32LittleEndian(report.AsSpan(30), 123456);
    WriteInt16(report, 34, 100);
    WriteInt16(report, 36, -200);
    WriteInt16(report, 38, 300);
    WriteInt16(report, 40, -400);
    WriteInt16(report, 42, 500);
    WriteInt16(report, 44, -600);

    Equal(true, SteamControllerProtocol.TryParseState(report, out var state));
    NotNull(state);
    Equal((byte)42, state!.Sequence);
    Equal(true, state.IsPressed(SteamButton.A));
    Equal(true, state.IsPressed(SteamButton.R4));
    Equal(true, state.IsPressed(SteamButton.QuickAccess));
    Equal(true, state.IsPressed(SteamButton.View));
    Equal(true, state.IsPressed(SteamButton.Menu));
    Equal(true, state.IsPressed(SteamButton.LeftGripTouch));
    Near(0.5f, state.LeftTrigger, 0.001f);
    Near(1, state.RightTrigger, 0.001f);
    Near(-1, state.LeftStick.X, 0.001f);
    Near(1, state.LeftStick.Y, 0.001f);
    Near(-0.25f, state.RightStick.Y, 0.001f);
    Equal(true, state.RightPad.Touched);
    Equal(true, state.RightPad.Clicked);
    Equal((uint)123456, state.Imu!.Value.Timestamp);
}

static void ParseViewAndMenu()
{
    var menuReport = new byte[30];
    menuReport[0] = SteamControllerProtocol.StateReport;
    menuReport[2] = 0x40;
    Equal(true, SteamControllerProtocol.TryParseState(menuReport, out var menuState));
    NotNull(menuState);
    Equal(true, menuState!.IsPressed(SteamButton.Menu));
    Equal(false, menuState.IsPressed(SteamButton.View));

    var viewReport = new byte[30];
    viewReport[0] = SteamControllerProtocol.StateReport;
    viewReport[3] = 0x40;
    Equal(true, SteamControllerProtocol.TryParseState(viewReport, out var viewState));
    NotNull(viewState);
    Equal(true, viewState!.IsPressed(SteamButton.View));
    Equal(false, viewState.IsPressed(SteamButton.Menu));
}

static void RejectInvalidReport()
{
    Equal(false, SteamControllerProtocol.TryParseState([0x99, 0, 0], out _));
}

static void ParseTimestampState()
{
    var report = new byte[46];
    report[0] = SteamControllerProtocol.TimestampStateReport;
    report[1] = 9;
    report[4] = 0x20; // Right pad touch.
    WriteInt16(report, 20, -12000);
    WriteInt16(report, 22, 16000);
    WriteUInt16(report, 24, 32000);
    WriteInt16(report, 26, 8000);
    WriteInt16(report, 28, -4000);
    WriteUInt16(report, 30, 48000);
    WriteUInt16(report, 32, 65500);
    WriteInt16(report, 34, 100);
    WriteInt16(report, 36, 200);
    WriteInt16(report, 38, 300);
    WriteInt16(report, 40, 400);
    WriteInt16(report, 42, 500);
    WriteInt16(report, 44, 600);

    Equal(true, SteamControllerProtocol.TryParseState(report, out var state));
    NotNull(state);
    Equal((byte)9, state!.Sequence);
    Equal(true, state.RightPad.Touched);
    Near(-12000 / 32768f, state.LeftPad.Position.X, 0.001f);
    Near(48000 / 65535f, state.RightPad.Contact, 0.001f);
    Equal((uint)65500, state.Imu!.Value.Timestamp);
}

static void ParseBattery()
{
    Equal(
        true,
        SteamControllerProtocol.TryParseBatteryStatus(
            [SteamControllerProtocol.BatteryReport, 2, 76],
            out var percent,
            out var charging));
    Equal(76, percent);
    Equal(true, charging);
}

static void BuildFeatureCommand()
{
    var report = SteamControllerProtocol.BuildFeatureCommand(
        SteamControllerProtocol.SetSettings,
        [7, 0, 0],
        64);
    Equal(64, report.Length);
    Equal((byte)1, report[0]);
    Equal((byte)0x87, report[1]);
    Equal((byte)3, report[2]);
    Equal((byte)7, report[3]);
}

static void ApplyDeadzone()
{
    Equal(Axis2.Zero, MappingEngine.ApplyRadialDeadzone(new Axis2(0.05f, 0), 0.1f));
    var mapped = MappingEngine.ApplyRadialDeadzone(new Axis2(0.55f, 0), 0.1f);
    Near(0.5f, mapped.X, 0.001f);
}

static void BuildVibrationReport()
{
    var report = SteamControllerProtocol.BuildHapticRumbleReport(0x1234, 0xABCD);
    Equal(10, report.Length);
    Equal(SteamControllerProtocol.HapticRumbleReport, report[0]);
    Equal((byte)0x34, report[4]);
    Equal((byte)0x12, report[5]);
    Equal((byte)0xCD, report[7]);
    Equal((byte)0xAB, report[8]);
}

static void BuildTrackpadHapticPulse()
{
    var report = SteamControllerProtocol.BuildHapticPulseReport(
        TrackpadHapticSide.Right,
        0x1234,
        0x5678,
        3);
    Equal(8, report.Length);
    Equal(SteamControllerProtocol.HapticPulseReport, report[0]);
    Equal((byte)TrackpadHapticSide.Right, report[1]);
    Equal((byte)0x34, report[2]);
    Equal((byte)0x12, report[3]);
    Equal((byte)0x78, report[4]);
    Equal((byte)0x56, report[5]);
    Equal((byte)3, report[6]);
}

static void CreateTrackpadHaptics()
{
    var engine = new TrackpadHapticEngine();
    var profile = MappingProfile.Default();
    Equal(
        0,
        engine.Update(
            new ControllerState
            {
                RightPad = new TrackpadState(Axis2.Zero, 1, true, false)
            },
            profile).Count);

    var motion = engine.Update(
        new ControllerState
        {
            RightPad = new TrackpadState(new Axis2(0.2f, 0), 1, true, false)
        },
        profile);
    Equal(1, motion.Count);
    Equal(TrackpadHapticSide.Right, motion[0].Side);
    Equal((ushort)398, motion[0].OnMicroseconds);

    var click = engine.Update(
        new ControllerState
        {
            RightPad = new TrackpadState(new Axis2(0.2f, 0), 1, true, true)
        },
        profile);
    Equal(1, click.Count);
    Equal((ushort)1, click[0].RepeatCount);
    Equal((ushort)1216, click[0].OnMicroseconds);

    var mutedEngine = new TrackpadHapticEngine();
    var mutedProfile = profile with
    {
        RightPad = profile.RightPad with { HapticIntensity = 0 }
    };
    Equal(
        0,
        mutedEngine.Update(
            new ControllerState
            {
                RightPad = new TrackpadState(Axis2.Zero, 1, true, true)
            },
            mutedProfile).Count);
}

static void ScaleTrackpadHaptics()
{
    static TrackpadHapticPulse CreateTick(float intensity)
    {
        var engine = new TrackpadHapticEngine();
        var profile = MappingProfile.Default() with
        {
            RightPad = MappingProfile.Default().RightPad with
            {
                HapticIntensity = intensity
            }
        };
        engine.Update(
            new ControllerState
            {
                RightPad = new TrackpadState(Axis2.Zero, 1, true, false)
            },
            profile);
        return engine.Update(
            new ControllerState
            {
                RightPad = new TrackpadState(new Axis2(0.2f, 0), 1, true, false)
            },
            profile)[0];
    }

    var low = CreateTick(0.05f);
    var high = CreateTick(1f);
    Equal((ushort)31, low.OnMicroseconds);
    Equal((ushort)612, high.OnMicroseconds);
    Equal(true, high.OnMicroseconds > low.OnMicroseconds * 10);
}

static void GlideTrackpadMouse()
{
    var engine = new MappingEngine();
    var profile = MappingProfile.Default() with
    {
        RightPad = new PadSettings
        {
            Mode = PadMode.Mouse,
            Sensitivity = 1,
            Deadzone = 0
        }
    };
    var start = DateTimeOffset.UtcNow;
    engine.Map(
        new ControllerState
        {
            RightPad = new TrackpadState(Axis2.Zero, 1, true, false),
            ReceivedAt = start
        },
        profile);
    var swipe = engine.Map(
        new ControllerState
        {
            RightPad = new TrackpadState(new Axis2(0.2f, 0), 1, true, false),
            ReceivedAt = start.AddMilliseconds(10)
        },
        profile);
    var release = engine.Map(
        new ControllerState { ReceivedAt = start.AddMilliseconds(20) },
        profile);
    var glide = engine.ContinueDesktopMotion(start.AddMilliseconds(30), profile);

    Near(7.2f, swipe.Desktop.MouseX, 0.001f);
    Equal(true, release.Desktop.MouseX > 0);
    Equal(true, glide.MouseX > 0);
    Equal(true, glide.MouseX < release.Desktop.MouseX);

    engine.Reset();
    var reset = engine.ContinueDesktopMotion(start.AddMilliseconds(40), profile);
    Equal(0f, reset.MouseX);
}

static void MapLayer()
{
    var profile = MappingProfile.Default() with
    {
        Layers =
        [
            new ActionLayer
            {
                Name = "Shift",
                HoldButton = SteamButton.L4,
                ButtonMappings = new() { [SteamButton.A] = VirtualButton.Y }
            }
        ]
    };
    var state = new ControllerState
    {
        Buttons = SteamButton.L4 | SteamButton.A | SteamButton.QuickAccess
    };
    var mapped = new MappingEngine().Map(state, profile);
    Equal(true, mapped.Gamepad.Buttons.HasFlag(VirtualButton.Y));
    Equal(false, mapped.Gamepad.Buttons.HasFlag(VirtualButton.A));
    Equal(true, mapped.Gamepad.Buttons.HasFlag(VirtualButton.QuickAccess));
}

static void MapRearButtons()
{
    var profile = MappingProfile.Default() with
    {
        ButtonMappings = new(MappingProfile.Default().ButtonMappings)
        {
            [SteamButton.L4] = VirtualButton.A,
            [SteamButton.L5] = VirtualButton.Y,
            [SteamButton.R4] = VirtualButton.Back,
            [SteamButton.R5] = VirtualButton.Start
        }
    };
    var state = new ControllerState
    {
        Buttons = SteamButton.L4 | SteamButton.L5 | SteamButton.R4 | SteamButton.R5
    };
    var mapped = new MappingEngine().Map(state, profile);

    Equal(true, mapped.Gamepad.Buttons.HasFlag(VirtualButton.A));
    Equal(true, mapped.Gamepad.Buttons.HasFlag(VirtualButton.Y));
    Equal(true, mapped.Gamepad.Buttons.HasFlag(VirtualButton.Back));
    Equal(true, mapped.Gamepad.Buttons.HasFlag(VirtualButton.Start));
    Equal(false, mapped.Gamepad.Buttons.HasFlag(VirtualButton.LeftStick));
    Equal(false, mapped.Gamepad.Buttons.HasFlag(VirtualButton.RightStick));
}

static void MapTrackpadDPad()
{
    var profile = MappingProfile.Default() with
    {
        LeftPad = new PadSettings { Mode = PadMode.DPad, Deadzone = 0 }
    };
    var state = new ControllerState
    {
        LeftPad = new TrackpadState(new Axis2(0.8f, 0.8f), 1, true, false)
    };
    var mapped = new MappingEngine().Map(state, profile);
    Equal(true, mapped.Gamepad.Buttons.HasFlag(VirtualButton.DPadUp));
    Equal(true, mapped.Gamepad.Buttons.HasFlag(VirtualButton.DPadRight));
}

static void WriteInt16(byte[] data, int offset, short value) =>
    BinaryPrimitives.WriteInt16LittleEndian(data.AsSpan(offset), value);

static void WriteUInt16(byte[] data, int offset, ushort value) =>
    BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(offset), value);

static void Equal<T>(T expected, T actual) where T : notnull
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
    {
        throw new InvalidOperationException($"expected {expected}, got {actual}");
    }
}

static void Near(float expected, float actual, float tolerance)
{
    if (Math.Abs(expected - actual) > tolerance)
    {
        throw new InvalidOperationException($"expected {expected}, got {actual}");
    }
}

static void NotNull(object? value)
{
    if (value is null)
    {
        throw new InvalidOperationException("expected a value, got null");
    }
}
