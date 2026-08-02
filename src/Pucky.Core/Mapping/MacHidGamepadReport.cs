using System.Buffers.Binary;

namespace Pucky.Core.Mapping;

/// <summary>
/// Encodes the compact HID report used by Pucky's macOS virtual gamepad.
/// The layout is compatible with the conventional Razer Serval mapping used
/// by SDL: 11 buttons, a hat switch, four stick axes, and two trigger axes.
/// </summary>
public static class MacHidGamepadReport
{
    public const int Length = 15;

    // This descriptor was constructed from the USB HID Usage Tables. It does
    // not contain report IDs: every submitted packet is one 15-byte input
    // report matching the fields below.
    private static readonly byte[] ReportDescriptorBytes =
    [
        0x05, 0x01,       // Usage Page (Generic Desktop)
        0x09, 0x05,       // Usage (Game Pad)
        0xA1, 0x01,       // Collection (Application)
        0x05, 0x09,       //   Usage Page (Button)
        0x19, 0x01,       //   Usage Minimum (Button 1)
        0x29, 0x0B,       //   Usage Maximum (Button 11)
        0x15, 0x00,       //   Logical Minimum (0)
        0x25, 0x01,       //   Logical Maximum (1)
        0x75, 0x01,       //   Report Size (1)
        0x95, 0x0B,       //   Report Count (11)
        0x81, 0x02,       //   Input (Data, Variable, Absolute)
        0x75, 0x01,       //   Report Size (1)
        0x95, 0x05,       //   Report Count (5)
        0x81, 0x03,       //   Input (Constant)
        0x05, 0x01,       //   Usage Page (Generic Desktop)
        0x09, 0x39,       //   Usage (Hat Switch)
        0x15, 0x00,       //   Logical Minimum (0)
        0x25, 0x07,       //   Logical Maximum (7)
        0x35, 0x00,       //   Physical Minimum (0)
        0x46, 0x3B, 0x01, //   Physical Maximum (315)
        0x65, 0x14,       //   Unit (Degrees)
        0x75, 0x04,       //   Report Size (4)
        0x95, 0x01,       //   Report Count (1)
        0x81, 0x42,       //   Input (Data, Variable, Absolute, Null State)
        0x65, 0x00,       //   Unit (None)
        0x75, 0x04,       //   Report Size (4)
        0x95, 0x01,       //   Report Count (1)
        0x81, 0x03,       //   Input (Constant)
        0x09, 0x30,       //   Usage (X: left stick X)
        0x09, 0x31,       //   Usage (Y: left stick Y)
        0x09, 0x32,       //   Usage (Z: right stick X)
        0x09, 0x33,       //   Usage (Rx: right stick Y)
        0x09, 0x34,       //   Usage (Ry: right trigger)
        0x09, 0x35,       //   Usage (Rz: left trigger)
        0x16, 0x00, 0x80, //   Logical Minimum (-32768)
        0x26, 0xFF, 0x7F, //   Logical Maximum (32767)
        0x75, 0x10,       //   Report Size (16)
        0x95, 0x06,       //   Report Count (6)
        0x81, 0x02,       //   Input (Data, Variable, Absolute)
        0xC0              // End Collection
    ];

    public static ReadOnlySpan<byte> ReportDescriptor => ReportDescriptorBytes;

    public static byte[] Encode(VirtualGamepadState state)
    {
        var report = new byte[Length];
        BinaryPrimitives.WriteUInt16LittleEndian(report, EncodeButtons(state.Buttons));
        report[2] = EncodeHat(state.Buttons);
        WriteAxis(report, 3, state.LeftStick.X);
        WriteAxis(report, 5, -state.LeftStick.Y);
        WriteAxis(report, 7, state.RightStick.X);
        WriteAxis(report, 9, -state.RightStick.Y);
        WriteTrigger(report, 11, state.RightTrigger);
        WriteTrigger(report, 13, state.LeftTrigger);
        return report;
    }

    private static ushort EncodeButtons(VirtualButton buttons)
    {
        ushort result = 0;
        SetButton(ref result, 0, buttons, VirtualButton.A);
        SetButton(ref result, 1, buttons, VirtualButton.B);
        SetButton(ref result, 2, buttons, VirtualButton.X);
        SetButton(ref result, 3, buttons, VirtualButton.Y);
        SetButton(ref result, 4, buttons, VirtualButton.LeftBumper);
        SetButton(ref result, 5, buttons, VirtualButton.RightBumper);
        SetButton(ref result, 6, buttons, VirtualButton.Back);
        SetButton(ref result, 7, buttons, VirtualButton.Start);
        SetButton(ref result, 8, buttons, VirtualButton.Guide);
        SetButton(ref result, 9, buttons, VirtualButton.LeftStick);
        SetButton(ref result, 10, buttons, VirtualButton.RightStick);
        return result;
    }

    private static void SetButton(
        ref ushort result,
        int bit,
        VirtualButton buttons,
        VirtualButton expected)
    {
        if (buttons.HasFlag(expected))
        {
            result |= (ushort)(1 << bit);
        }
    }

    private static byte EncodeHat(VirtualButton buttons)
    {
        var up = buttons.HasFlag(VirtualButton.DPadUp);
        var down = buttons.HasFlag(VirtualButton.DPadDown);
        var left = buttons.HasFlag(VirtualButton.DPadLeft);
        var right = buttons.HasFlag(VirtualButton.DPadRight);

        if (up == down) up = down = false;
        if (left == right) left = right = false;

        if (up && right) return 1;
        if (right && down) return 3;
        if (down && left) return 5;
        if (left && up) return 7;
        if (up) return 0;
        if (right) return 2;
        if (down) return 4;
        if (left) return 6;
        return 8;
    }

    private static void WriteAxis(byte[] report, int offset, float value)
    {
        var scaled = (short)Math.Round(Math.Clamp(value, -1, 1) * 32767f);
        BinaryPrimitives.WriteInt16LittleEndian(report.AsSpan(offset), scaled);
    }

    private static void WriteTrigger(byte[] report, int offset, float value)
    {
        var scaled = (int)Math.Round(Math.Clamp(value, 0, 1) * 65535f) - 32768;
        BinaryPrimitives.WriteInt16LittleEndian(report.AsSpan(offset), (short)scaled);
    }
}
