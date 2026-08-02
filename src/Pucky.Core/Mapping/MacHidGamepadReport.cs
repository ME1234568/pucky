using System.Buffers.Binary;

namespace Pucky.Core.Mapping;

/// <summary>
/// Encodes the compact input report consumed by Pucky's native macOS HID
/// helper. The layout is compatible with the conventional Razer Serval
/// mapping used by SDL: 11 buttons, a hat switch, four stick axes, and two
/// trigger axes.
/// </summary>
public static class MacHidGamepadReport
{
    public const int Length = 15;

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
