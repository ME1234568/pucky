using System.Buffers.Binary;
using Pucky.Core.Input;

namespace Pucky.Core.Mapping;

/// <summary>
/// Encodes ViGEmBus's 63-byte DS4_REPORT_EX (a DualShock 4 USB input report
/// without its report ID), including motion data converted from Triton IMU
/// units into DualShock 4 units and axes.
/// </summary>
public static class DualShock4Report
{
    public const int Length = 63;

    // Triton: +/-2000 deg/s and +/-2 g over the full int16 range, i.e.
    // 16.384 LSB per deg/s and 16384 LSB per g. DualShock 4 nominally reports
    // 16 LSB per deg/s and 8192 LSB per g.
    private const float GyroScale = 32768f * (16f / 16.384f);
    private const float AccelScale = 32768f * (8192f / 16384f);

    public const ushort Square = 1 << 4;
    public const ushort Cross = 1 << 5;
    public const ushort Circle = 1 << 6;
    public const ushort Triangle = 1 << 7;
    public const ushort L1 = 1 << 8;
    public const ushort R1 = 1 << 9;
    public const ushort L2 = 1 << 10;
    public const ushort R2 = 1 << 11;
    public const ushort Share = 1 << 12;
    public const ushort Options = 1 << 13;
    public const ushort L3 = 1 << 14;
    public const ushort R3 = 1 << 15;
    public const byte SpecialPs = 1 << 0;
    public const byte SpecialTouchpad = 1 << 1;

    public static byte[] Encode(VirtualGamepadState state, ushort timestamp = 0)
    {
        var report = new byte[Length];
        Encode(state, timestamp, report);
        return report;
    }

    public static void Encode(VirtualGamepadState state, ushort timestamp, Span<byte> report)
    {
        if (report.Length < Length)
        {
            throw new ArgumentException($"Report buffer must be at least {Length} bytes.", nameof(report));
        }

        report[..Length].Clear();
        report[0] = ToStickByte(state.LeftStick.X);
        report[1] = ToStickByte(-state.LeftStick.Y);
        report[2] = ToStickByte(state.RightStick.X);
        report[3] = ToStickByte(-state.RightStick.Y);

        var buttons = state.Buttons;
        ushort face = EncodeHat(buttons);
        // Positions mirror the Xbox 360 and uinput outputs, which emit
        // VirtualButton.Y on the west face button and X on the north one.
        if (buttons.HasFlag(VirtualButton.Y)) face |= Square;
        if (buttons.HasFlag(VirtualButton.A)) face |= Cross;
        if (buttons.HasFlag(VirtualButton.B)) face |= Circle;
        if (buttons.HasFlag(VirtualButton.X)) face |= Triangle;
        if (buttons.HasFlag(VirtualButton.LeftBumper)) face |= L1;
        if (buttons.HasFlag(VirtualButton.RightBumper)) face |= R1;
        if (state.LeftTrigger > 0.02f) face |= L2;
        if (state.RightTrigger > 0.02f) face |= R2;
        if (buttons.HasFlag(VirtualButton.Back)) face |= Share;
        if (buttons.HasFlag(VirtualButton.Start)) face |= Options;
        if (buttons.HasFlag(VirtualButton.LeftStick)) face |= L3;
        if (buttons.HasFlag(VirtualButton.RightStick)) face |= R3;
        BinaryPrimitives.WriteUInt16LittleEndian(report[4..], face);

        byte special = 0;
        if (buttons.HasFlag(VirtualButton.Guide)) special |= SpecialPs;
        if (buttons.HasFlag(VirtualButton.QuickAccess)) special |= SpecialTouchpad;
        report[6] = special;
        report[7] = ToTriggerByte(state.LeftTrigger);
        report[8] = ToTriggerByte(state.RightTrigger);
        BinaryPrimitives.WriteUInt16LittleEndian(report[9..], timestamp);
        report[11] = 0xFF;

        if (state.Motion is { } imu)
        {
            // Triton (X, Y, Z) maps to the DualShock 4 / SDL frame as
            // (X, Z, -Y): Triton reports gravity on Z when lying flat.
            WriteMotion(report, 12, imu.GyroscopeXY.X, GyroScale);
            WriteMotion(report, 14, imu.GyroscopeZ, GyroScale);
            WriteMotion(report, 16, -imu.GyroscopeXY.Y, GyroScale);
            WriteMotion(report, 18, imu.AccelerometerXY.X, AccelScale);
            WriteMotion(report, 20, imu.AccelerometerZ, AccelScale);
            WriteMotion(report, 22, -imu.AccelerometerXY.Y, AccelScale);
        }
        else
        {
            // A controller resting flat: 1 g upward on the DS4 Y axis.
            BinaryPrimitives.WriteInt16LittleEndian(report[20..], 8192);
        }

        // Battery: full, on battery power.
        report[29] = 0x0A;
        // One touch packet with both fingers lifted (bit 7 set).
        report[32] = 1;
        report[34] = 0x80;
        report[38] = 0x80;
        report[43] = 0x80;
        report[47] = 0x80;
        report[52] = 0x80;
        report[56] = 0x80;
    }

    private static byte EncodeHat(VirtualButton buttons)
    {
        var up = buttons.HasFlag(VirtualButton.DPadUp);
        var down = buttons.HasFlag(VirtualButton.DPadDown);
        var left = buttons.HasFlag(VirtualButton.DPadLeft);
        var right = buttons.HasFlag(VirtualButton.DPadRight);
        if (up && down) { up = false; down = false; }
        if (left && right) { left = false; right = false; }

        return (up, right, down, left) switch
        {
            (true, false, false, false) => 0,
            (true, true, false, false) => 1,
            (false, true, false, false) => 2,
            (false, true, true, false) => 3,
            (false, false, true, false) => 4,
            (false, false, true, true) => 5,
            (false, false, false, true) => 6,
            (true, false, false, true) => 7,
            _ => 8
        };
    }

    private static void WriteMotion(Span<byte> report, int offset, float normalized, float scale) =>
        BinaryPrimitives.WriteInt16LittleEndian(
            report[offset..],
            (short)Math.Clamp(MathF.Round(normalized * scale), short.MinValue, short.MaxValue));

    private static byte ToStickByte(float value) =>
        (byte)Math.Clamp(MathF.Round((Math.Clamp(value, -1, 1) + 1) * 127.5f), 0, 255);

    private static byte ToTriggerByte(float value) =>
        (byte)MathF.Round(Math.Clamp(value, 0, 1) * 255f);
}
