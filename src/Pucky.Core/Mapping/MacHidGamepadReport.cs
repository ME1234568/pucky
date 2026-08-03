using System.Buffers.Binary;

namespace Pucky.Core.Mapping;

/// <summary>
/// Encodes the Razer Serval-compatible raw HID report consumed by Pucky's
/// native macOS helper. Its sparse buttons and six signed axes match the
/// standard-gamepad mapping Chromium applies to that VID/PID on macOS.
/// </summary>
public static class MacHidGamepadReport
{
    public const int ProtocolVersion = 5;
    public const int Length = 15;

    public static byte[] Encode(VirtualGamepadState state)
    {
        var report = new byte[Length];
        EncodeButtons(report, state.Buttons);
        report[2] = EncodeHat(state.Buttons);
        WriteAxis(report, 3, state.LeftStick.X);
        WriteAxis(report, 5, -state.LeftStick.Y);
        WriteAxis(report, 7, state.RightStick.X);
        WriteTrigger(report, 9, state.LeftTrigger);
        WriteTrigger(report, 11, state.RightTrigger);
        WriteAxis(report, 13, -state.RightStick.Y);
        return report;
    }

    private static void EncodeButtons(byte[] report, VirtualButton buttons)
    {
        SetButton(report, 0, buttons, VirtualButton.A);
        SetButton(report, 1, buttons, VirtualButton.B);
        SetButton(report, 3, buttons, VirtualButton.X);
        SetButton(report, 4, buttons, VirtualButton.Y);
        SetButton(report, 6, buttons, VirtualButton.LeftBumper);
        SetButton(report, 7, buttons, VirtualButton.RightBumper);
        SetButton(report, 10, buttons, VirtualButton.Back);
        SetButton(report, 11, buttons, VirtualButton.Start);
        SetButton(report, 12, buttons, VirtualButton.Guide);
        SetButton(report, 13, buttons, VirtualButton.LeftStick);
        SetButton(report, 14, buttons, VirtualButton.RightStick);
        SetButton(report, 15, buttons, VirtualButton.QuickAccess);
    }

    private static void SetButton(
        byte[] report,
        int bit,
        VirtualButton buttons,
        VirtualButton expected)
    {
        if (buttons.HasFlag(expected))
        {
            report[bit / 8] |= (byte)(1 << (bit % 8));
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
        // Chromium normalizes each raw axis to -1..1 before converting these
        // two axes to standard 0..1 trigger buttons.
        var scaled = (short)Math.Round(
            (Math.Clamp(value, 0, 1) * 65535f) + short.MinValue);
        BinaryPrimitives.WriteInt16LittleEndian(report.AsSpan(offset), scaled);
    }
}
