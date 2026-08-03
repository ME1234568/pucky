using System.Buffers.Binary;

namespace Pucky.Core.Input;

public static class SteamControllerProtocol
{
    public const int ValveVendorId = 0x28DE;
    public const int WiredProductId = 0x1302;
    public const int BluetoothProductId = 0x1303;
    public const int PuckProductId = 0x1304;
    public const int VendorUsagePage = 0xFF00;

    public const byte StateReport = 0x45;
    public const byte LegacyStateReport = 0x42;
    public const byte BatteryReport = 0x43;
    public const byte StatusReport = 0x44;
    public const byte TimestampStateReport = 0x47;

    public const byte FeatureReport = 0x01;
    public const byte AlternateFeatureReport = 0x02;
    public const byte ClearDigitalMappings = 0x81;
    public const byte SetDefaultMappings = 0x85;
    public const byte SetSettings = 0x87;
    public const byte LoadDefaultSettings = 0x8E;

    public const byte SettingLeftTrackpadMode = 0x07;
    public const byte SettingRightTrackpadMode = 0x08;
    public const byte SettingLizardMode = 0x09;
    public const byte SettingImuMode = 0x30;
    public const byte TrackpadNone = 0x07;
    public const ushort LizardModeOff = 0;
    public const ushort ImuRawAccelerometerGyroscope = 0x0018;
    public const byte HapticRumbleReport = 0x80;
    public const byte HapticPulseReport = 0x81;

    public static bool IsSupportedProduct(int productId) =>
        productId is WiredProductId or BluetoothProductId or PuckProductId;

    public static bool TryParseBatteryStatus(
        ReadOnlySpan<byte> report,
        out int batteryPercent,
        out bool charging)
    {
        batteryPercent = 0;
        charging = false;
        if (report.Length < 3 || report[0] != BatteryReport)
        {
            return false;
        }

        batteryPercent = Math.Clamp((int)report[2], 0, 100);
        charging = report[1] is 2 or 4;
        return true;
    }

    public static bool TryParseState(ReadOnlySpan<byte> report, out ControllerState? state)
    {
        state = null;
        var timestampFormat = report.Length > 0 && report[0] == TimestampStateReport;
        var minimumLength = timestampFormat ? 32 : 30;
        if (report.Length < minimumLength ||
            report[0] is not (StateReport or LegacyStateReport or TimestampStateReport))
        {
            return false;
        }

        SteamButton buttons = SteamButton.None;
        var b0 = report[2];
        var b1 = report[3];
        var b2 = report[4];
        var b3 = report[5];

        Add(ref buttons, b0, 0x01, SteamButton.A);
        Add(ref buttons, b0, 0x02, SteamButton.B);
        Add(ref buttons, b0, 0x04, SteamButton.X);
        Add(ref buttons, b0, 0x08, SteamButton.Y);
        Add(ref buttons, b0, 0x10, SteamButton.QuickAccess);
        Add(ref buttons, b0, 0x20, SteamButton.RightStick);
        Add(ref buttons, b0, 0x40, SteamButton.Menu);
        Add(ref buttons, b0, 0x80, SteamButton.R4);

        Add(ref buttons, b1, 0x01, SteamButton.R5);
        Add(ref buttons, b1, 0x02, SteamButton.RightBumper);
        Add(ref buttons, b1, 0x04, SteamButton.DPadDown);
        Add(ref buttons, b1, 0x08, SteamButton.DPadRight);
        Add(ref buttons, b1, 0x10, SteamButton.DPadLeft);
        Add(ref buttons, b1, 0x20, SteamButton.DPadUp);
        Add(ref buttons, b1, 0x40, SteamButton.View);
        Add(ref buttons, b1, 0x80, SteamButton.LeftStick);

        Add(ref buttons, b2, 0x01, SteamButton.Steam);
        Add(ref buttons, b2, 0x02, SteamButton.L4);
        Add(ref buttons, b2, 0x04, SteamButton.L5);
        Add(ref buttons, b2, 0x08, SteamButton.LeftBumper);
        Add(ref buttons, b2, 0x10, SteamButton.RightStickTouch);
        Add(ref buttons, b2, 0x20, SteamButton.RightPadTouch);
        Add(ref buttons, b2, 0x40, SteamButton.RightPadClick);
        Add(ref buttons, b2, 0x80, SteamButton.RightTriggerFull);

        Add(ref buttons, b3, 0x01, SteamButton.LeftStickTouch);
        Add(ref buttons, b3, 0x02, SteamButton.LeftPadTouch);
        Add(ref buttons, b3, 0x04, SteamButton.LeftPadClick);
        Add(ref buttons, b3, 0x08, SteamButton.LeftTriggerFull);
        Add(ref buttons, b3, 0x10, SteamButton.RightGripTouch);
        Add(ref buttons, b3, 0x20, SteamButton.LeftGripTouch);

        var leftPadTouched = buttons.HasFlag(SteamButton.LeftPadTouch);
        var rightPadTouched = buttons.HasFlag(SteamButton.RightPadTouch);
        var leftPadOffset = timestampFormat ? 20 : 18;
        var rightPadOffset = timestampFormat ? 26 : 24;

        state = new ControllerState
        {
            Sequence = report[1],
            Buttons = buttons,
            LeftTrigger = NormalizeTrigger(ReadUInt16(report, 6)),
            RightTrigger = NormalizeTrigger(ReadUInt16(report, 8)),
            // Triton stick Y already uses the XInput convention (up is positive).
            LeftStick = ReadAxis(report, 10, invertY: false),
            RightStick = ReadAxis(report, 14, invertY: false),
            // Triton pad Y, like its sticks, is positive toward the top.
            LeftPad = new TrackpadState(
                ReadAxis(report, leftPadOffset, invertY: false),
                NormalizeUnsigned(ReadUInt16(report, leftPadOffset + 4)),
                leftPadTouched,
                buttons.HasFlag(SteamButton.LeftPadClick)),
            RightPad = new TrackpadState(
                ReadAxis(report, rightPadOffset, invertY: false),
                NormalizeUnsigned(ReadUInt16(report, rightPadOffset + 4)),
                rightPadTouched,
                buttons.HasFlag(SteamButton.RightPadClick)),
            Imu = TryReadImu(report, timestampFormat),
            ReceivedAt = DateTimeOffset.UtcNow
        };

        return true;
    }

    public static byte[] BuildFeatureCommand(
        byte command,
        ReadOnlySpan<byte> payload,
        int reportLength,
        byte reportId = FeatureReport)
    {
        if (reportLength < payload.Length + 3)
        {
            throw new ArgumentOutOfRangeException(nameof(reportLength));
        }

        if (payload.Length > byte.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(payload));
        }

        var report = new byte[reportLength];
        report[0] = reportId;
        report[1] = command;
        report[2] = (byte)payload.Length;
        payload.CopyTo(report.AsSpan(3));
        return report;
    }

    public static byte[] BuildDisableLizardSettingsPayload() =>
    [
        SettingLizardMode, (byte)(LizardModeOff & 0xFF), (byte)(LizardModeOff >> 8),
        SettingImuMode, (byte)(ImuRawAccelerometerGyroscope & 0xFF),
        (byte)(ImuRawAccelerometerGyroscope >> 8)
    ];

    public static byte[] BuildHapticRumbleReport(
        ushort leftMotorSpeed,
        ushort rightMotorSpeed)
    {
        // Triton haptic report: ID, type, intensity, left(speed, gain),
        // right(speed, gain). Type/intensity/gain zero selects firmware defaults.
        var report = new byte[10];
        report[0] = HapticRumbleReport;
        BinaryPrimitives.WriteUInt16LittleEndian(report.AsSpan(2), 0);
        BinaryPrimitives.WriteUInt16LittleEndian(report.AsSpan(4), leftMotorSpeed);
        report[6] = 0;
        BinaryPrimitives.WriteUInt16LittleEndian(report.AsSpan(7), rightMotorSpeed);
        report[9] = 0;
        return report;
    }

    public static byte[] BuildHapticPulseReport(
        TrackpadHapticSide side,
        ushort onMicroseconds,
        ushort offMicroseconds,
        ushort repeatCount)
    {
        var report = new byte[8];
        report[0] = HapticPulseReport;
        report[1] = (byte)side;
        BinaryPrimitives.WriteUInt16LittleEndian(report.AsSpan(2), onMicroseconds);
        BinaryPrimitives.WriteUInt16LittleEndian(report.AsSpan(4), offMicroseconds);
        BinaryPrimitives.WriteUInt16LittleEndian(report.AsSpan(6), repeatCount);
        return report;
    }

    private static void Add(ref SteamButton buttons, byte value, byte mask, SteamButton button)
    {
        if ((value & mask) != 0)
        {
            buttons |= button;
        }
    }

    private static short ReadInt16(ReadOnlySpan<byte> data, int offset) =>
        BinaryPrimitives.ReadInt16LittleEndian(data[offset..]);

    private static ushort ReadUInt16(ReadOnlySpan<byte> data, int offset) =>
        BinaryPrimitives.ReadUInt16LittleEndian(data[offset..]);

    private static uint ReadUInt32(ReadOnlySpan<byte> data, int offset) =>
        BinaryPrimitives.ReadUInt32LittleEndian(data[offset..]);

    private static Axis2 ReadAxis(ReadOnlySpan<byte> data, int offset, bool invertY)
    {
        var x = NormalizeSigned(ReadInt16(data, offset));
        var y = NormalizeSigned(ReadInt16(data, offset + 2));
        return new Axis2(x, invertY ? -y : y);
    }

    private static ImuState? TryReadImu(ReadOnlySpan<byte> report, bool timestampFormat)
    {
        if (report.Length < 46)
        {
            return null;
        }

        var timestamp = timestampFormat
            ? ReadUInt16(report, 32)
            : ReadUInt32(report, 30);
        const int sensorOffset = 34;
        return new ImuState(
            timestamp,
            new Axis2(
                NormalizeSigned(ReadInt16(report, sensorOffset)),
                NormalizeSigned(ReadInt16(report, sensorOffset + 2))),
            NormalizeSigned(ReadInt16(report, sensorOffset + 4)),
            new Axis2(
                NormalizeSigned(ReadInt16(report, sensorOffset + 6)),
                NormalizeSigned(ReadInt16(report, sensorOffset + 8))),
            NormalizeSigned(ReadInt16(report, sensorOffset + 10)));
    }

    // Triton trigger values are unsigned and reach full pull near 0x8000.
    private static float NormalizeTrigger(ushort value) =>
        Math.Clamp(value / 32767f, 0f, 1f);

    private static float NormalizeSigned(short value) =>
        value < 0 ? value / 32768f : value / 32767f;

    private static float NormalizeUnsigned(ushort value) => value / 65535f;
}
