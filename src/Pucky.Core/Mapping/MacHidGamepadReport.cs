using System.Buffers.Binary;

namespace Pucky.Core.Mapping;

/// <summary>
/// Encodes an Xbox One S (Model 1708) Bluetooth state report followed by an
/// internal Guide-button byte consumed by Pucky's native macOS HID helper.
/// The helper publishes the state as report ID 1 and Guide as report ID 2.
/// </summary>
public static class MacHidGamepadReport
{
    public const int ProtocolVersion = 4;
    public const int Length = 18;

    public static byte[] Encode(VirtualGamepadState state)
    {
        var report = new byte[Length];
        report[0] = 0x01;
        EncodeButtons(report, state.Buttons);
        WriteAxis(report, 1, state.LeftStick.X);
        WriteAxis(report, 3, -state.LeftStick.Y);
        WriteAxis(report, 5, state.RightStick.X);
        WriteAxis(report, 7, -state.RightStick.Y);
        WriteTrigger(report, 9, state.LeftTrigger);
        WriteTrigger(report, 11, state.RightTrigger);
        report[13] = EncodeHat(state.Buttons);
        report[17] = state.Buttons.HasFlag(VirtualButton.Guide) ? (byte)1 : (byte)0;
        return report;
    }

    private static void EncodeButtons(byte[] report, VirtualButton buttons)
    {
        SetButton(report, 14, 0, buttons, VirtualButton.A);
        SetButton(report, 14, 1, buttons, VirtualButton.B);
        SetButton(report, 14, 3, buttons, VirtualButton.X);
        SetButton(report, 14, 4, buttons, VirtualButton.Y);
        SetButton(report, 14, 6, buttons, VirtualButton.LeftBumper);
        SetButton(report, 14, 7, buttons, VirtualButton.RightBumper);
        SetButton(report, 15, 3, buttons, VirtualButton.Start);
        SetButton(report, 15, 4, buttons, VirtualButton.Guide);
        SetButton(report, 15, 5, buttons, VirtualButton.LeftStick);
        SetButton(report, 15, 6, buttons, VirtualButton.RightStick);
        SetButton(report, 16, 0, buttons, VirtualButton.Back);
    }

    private static void SetButton(
        byte[] report,
        int offset,
        int bit,
        VirtualButton buttons,
        VirtualButton expected)
    {
        if (buttons.HasFlag(expected))
        {
            report[offset] |= (byte)(1 << bit);
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

        if (up && right) return 2;
        if (right && down) return 4;
        if (down && left) return 6;
        if (left && up) return 8;
        if (up) return 1;
        if (right) return 3;
        if (down) return 5;
        if (left) return 7;
        return 0;
    }

    private static void WriteAxis(byte[] report, int offset, float value)
    {
        var scaled = (ushort)Math.Round(
            (Math.Clamp(value, -1, 1) + 1f) * 32767.5f);
        BinaryPrimitives.WriteUInt16LittleEndian(report.AsSpan(offset), scaled);
    }

    private static void WriteTrigger(byte[] report, int offset, float value)
    {
        var scaled = (ushort)Math.Round(Math.Clamp(value, 0, 1) * 1023f);
        BinaryPrimitives.WriteUInt16LittleEndian(report.AsSpan(offset), scaled);
    }
}
