using Pucky.Core.Input;

namespace Pucky.Core.Mapping;

public sealed record VirtualGamepadState(
    VirtualButton Buttons,
    Axis2 LeftStick,
    Axis2 RightStick,
    float LeftTrigger,
    float RightTrigger);

public sealed record DesktopState(
    float MouseX,
    float MouseY,
    float ScrollX,
    float ScrollY,
    bool LeftClick,
    bool RightClick);

public sealed record MappingResult(VirtualGamepadState Gamepad, DesktopState Desktop);

public sealed class MappingEngine
{
    private Axis2 _lastLeftPad;
    private Axis2 _lastRightPad;
    private bool _hadLeftTouch;
    private bool _hadRightTouch;

    public MappingResult Map(ControllerState input, MappingProfile profile)
    {
        var buttons = MapButtons(input, profile);
        var leftStick = ApplyRadialDeadzone(input.LeftStick, profile.LeftStickDeadzone);
        var rightStick = ApplyRadialDeadzone(input.RightStick, profile.RightStickDeadzone);
        var desktop = new DesktopState(0, 0, 0, 0, false, false);

        ApplyPad(
            input.LeftPad,
            profile.LeftPad,
            ref leftStick,
            ref rightStick,
            ref buttons,
            ref desktop,
            ref _lastLeftPad,
            ref _hadLeftTouch);

        ApplyPad(
            input.RightPad,
            profile.RightPad,
            ref leftStick,
            ref rightStick,
            ref buttons,
            ref desktop,
            ref _lastRightPad,
            ref _hadRightTouch);

        ApplyGyro(input, profile.Gyro, ref rightStick, ref desktop);

        return new MappingResult(
            new VirtualGamepadState(
                buttons,
                ClampAxis(leftStick),
                ClampAxis(rightStick),
                Math.Clamp(input.LeftTrigger, 0, 1),
                Math.Clamp(input.RightTrigger, 0, 1)),
            desktop);
    }

    public void Reset()
    {
        _lastLeftPad = Axis2.Zero;
        _lastRightPad = Axis2.Zero;
        _hadLeftTouch = false;
        _hadRightTouch = false;
    }

    public static Axis2 ApplyRadialDeadzone(Axis2 value, float deadzone)
    {
        deadzone = Math.Clamp(deadzone, 0, 0.95f);
        var length = value.Length;
        if (length <= deadzone || length == 0)
        {
            return Axis2.Zero;
        }

        var scaledLength = Math.Clamp((length - deadzone) / (1 - deadzone), 0, 1);
        var factor = scaledLength / length;
        return new Axis2(value.X * factor, value.Y * factor);
    }

    private static VirtualButton MapButtons(ControllerState input, MappingProfile profile)
    {
        var mappings = profile.ButtonMappings;
        foreach (var layer in profile.Layers)
        {
            if (layer.HoldButton != SteamButton.None && input.IsPressed(layer.HoldButton))
            {
                mappings = MergeMappings(mappings, layer.ButtonMappings);
            }
        }

        VirtualButton output = VirtualButton.None;
        foreach (var (source, target) in mappings)
        {
            if (input.IsPressed(source))
            {
                output |= target;
            }
        }

        return output;
    }

    private static Dictionary<SteamButton, VirtualButton> MergeMappings(
        Dictionary<SteamButton, VirtualButton> baseMappings,
        Dictionary<SteamButton, VirtualButton> overrides)
    {
        var merged = new Dictionary<SteamButton, VirtualButton>(baseMappings);
        foreach (var mapping in overrides)
        {
            merged[mapping.Key] = mapping.Value;
        }

        return merged;
    }

    private static void ApplyPad(
        TrackpadState pad,
        PadSettings settings,
        ref Axis2 leftStick,
        ref Axis2 rightStick,
        ref VirtualButton buttons,
        ref DesktopState desktop,
        ref Axis2 lastPosition,
        ref bool hadTouch)
    {
        var active = pad.Touched && (!settings.ClickRequired || pad.Clicked);
        if (!active)
        {
            hadTouch = false;
            return;
        }

        var value = ApplyRadialDeadzone(pad.Position, settings.Deadzone);
        value = new Axis2(
            value.X * settings.Sensitivity,
            value.Y * settings.Sensitivity);

        switch (settings.Mode)
        {
            case PadMode.Joystick:
                rightStick = value;
                break;
            case PadMode.Mouse:
            {
                var delta = hadTouch
                    ? new Axis2(pad.Position.X - lastPosition.X, pad.Position.Y - lastPosition.Y)
                    : Axis2.Zero;
                desktop = desktop with
                {
                    MouseX = desktop.MouseX + (delta.X * settings.Sensitivity * 36f),
                    MouseY = desktop.MouseY + (delta.Y * settings.Sensitivity * 36f),
                    LeftClick = desktop.LeftClick || pad.Clicked
                };
                break;
            }
            case PadMode.DPad:
                buttons |= ToDPad(value);
                break;
            case PadMode.Scroll:
            {
                var delta = hadTouch
                    ? new Axis2(pad.Position.X - lastPosition.X, pad.Position.Y - lastPosition.Y)
                    : Axis2.Zero;
                desktop = desktop with
                {
                    ScrollX = desktop.ScrollX + (delta.X * settings.Sensitivity * 8f),
                    ScrollY = desktop.ScrollY + (delta.Y * settings.Sensitivity * 8f)
                };
                break;
            }
            case PadMode.Disabled:
            default:
                break;
        }

        lastPosition = pad.Position;
        hadTouch = true;
    }

    private static VirtualButton ToDPad(Axis2 value)
    {
        VirtualButton buttons = VirtualButton.None;
        if (value.Y > 0.35f) buttons |= VirtualButton.DPadUp;
        if (value.Y < -0.35f) buttons |= VirtualButton.DPadDown;
        if (value.X < -0.35f) buttons |= VirtualButton.DPadLeft;
        if (value.X > 0.35f) buttons |= VirtualButton.DPadRight;
        return buttons;
    }

    private static void ApplyGyro(
        ControllerState input,
        GyroSettings settings,
        ref Axis2 rightStick,
        ref DesktopState desktop)
    {
        if (settings.Mode == GyroMode.Disabled ||
            input.Imu is null ||
            (settings.EnableButton != SteamButton.None && !input.IsPressed(settings.EnableButton)))
        {
            return;
        }

        var gyro = ApplyRadialDeadzone(input.Imu.Value.GyroscopeXY, settings.Deadzone);
        gyro = new Axis2(
            gyro.Y * settings.Sensitivity,
            -gyro.X * settings.Sensitivity);

        if (settings.Mode == GyroMode.RightStick)
        {
            rightStick = ClampAxis(new Axis2(rightStick.X + gyro.X, rightStick.Y + gyro.Y));
        }
        else if (settings.Mode == GyroMode.Mouse)
        {
            desktop = desktop with
            {
                MouseX = desktop.MouseX + (gyro.X * 12f),
                MouseY = desktop.MouseY + (gyro.Y * 12f)
            };
        }
    }

    private static Axis2 ClampAxis(Axis2 value) =>
        new(Math.Clamp(value.X, -1, 1), Math.Clamp(value.Y, -1, 1));
}
