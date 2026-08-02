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
    private const float MouseScale = 36f;
    private const float MouseVelocityBlend = 0.65f;
    private const float MouseGlideFriction = 8f;
    private const float MouseGlideStopSpeed = 5f;
    private const float MouseMaxGlideSpeed = 5000f;
    private PadMotion _leftPadMotion;
    private PadMotion _rightPadMotion;

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
            input.ReceivedAt,
            ref _leftPadMotion);

        ApplyPad(
            input.RightPad,
            profile.RightPad,
            ref leftStick,
            ref rightStick,
            ref buttons,
            ref desktop,
            input.ReceivedAt,
            ref _rightPadMotion);

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

    public DesktopState ContinueDesktopMotion(
        DateTimeOffset timestamp,
        MappingProfile profile)
    {
        var desktop = new DesktopState(0, 0, 0, 0, false, false);
        if (profile.LeftPad.Mode == PadMode.Mouse)
        {
            ApplyMouseGlide(timestamp, ref desktop, ref _leftPadMotion);
        }
        if (profile.RightPad.Mode == PadMode.Mouse)
        {
            ApplyMouseGlide(timestamp, ref desktop, ref _rightPadMotion);
        }
        return desktop;
    }

    public void Reset()
    {
        _leftPadMotion = default;
        _rightPadMotion = default;
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
        DateTimeOffset timestamp,
        ref PadMotion motion)
    {
        var active = pad.Touched && (!settings.ClickRequired || pad.Clicked);
        if (settings.Mode == PadMode.Mouse)
        {
            ApplyMousePad(pad, settings, active, timestamp, ref desktop, ref motion);
            return;
        }

        motion.Velocity = Axis2.Zero;
        motion.Gliding = false;
        if (!active)
        {
            motion.HadTouch = false;
            motion.LastUpdate = timestamp;
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
            case PadMode.DPad:
                buttons |= ToDPad(value);
                break;
            case PadMode.Scroll:
            {
                var delta = motion.HadTouch
                    ? new Axis2(
                        pad.Position.X - motion.LastPosition.X,
                        pad.Position.Y - motion.LastPosition.Y)
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

        motion.LastPosition = pad.Position;
        motion.LastUpdate = timestamp;
        motion.HadTouch = true;
    }

    private static void ApplyMousePad(
        TrackpadState pad,
        PadSettings settings,
        bool active,
        DateTimeOffset timestamp,
        ref DesktopState desktop,
        ref PadMotion motion)
    {
        var elapsed = ElapsedSeconds(motion.LastUpdate, timestamp);
        if (active)
        {
            var delta = motion.HadTouch
                ? new Axis2(
                    pad.Position.X - motion.LastPosition.X,
                    pad.Position.Y - motion.LastPosition.Y)
                : Axis2.Zero;
            var movement = new Axis2(
                delta.X * settings.Sensitivity * MouseScale,
                delta.Y * settings.Sensitivity * MouseScale);

            if (motion.HadTouch && elapsed > 0)
            {
                var instantaneous = ClampLength(
                    new Axis2(movement.X / elapsed, movement.Y / elapsed),
                    MouseMaxGlideSpeed);
                motion.Velocity = motion.Velocity.Length == 0
                    ? instantaneous
                    : new Axis2(
                        (motion.Velocity.X * (1 - MouseVelocityBlend)) +
                        (instantaneous.X * MouseVelocityBlend),
                        (motion.Velocity.Y * (1 - MouseVelocityBlend)) +
                        (instantaneous.Y * MouseVelocityBlend));
            }
            else
            {
                motion.Velocity = Axis2.Zero;
            }

            desktop = desktop with
            {
                MouseX = desktop.MouseX + movement.X,
                MouseY = desktop.MouseY + movement.Y,
                LeftClick = desktop.LeftClick || pad.Clicked
            };
            motion.LastPosition = pad.Position;
            motion.LastUpdate = timestamp;
            motion.HadTouch = true;
            motion.Gliding = false;
            return;
        }

        var released = motion.HadTouch && !pad.Touched;
        motion.HadTouch = false;
        if (pad.Touched)
        {
            motion.Velocity = Axis2.Zero;
            motion.Gliding = false;
            motion.LastUpdate = timestamp;
            return;
        }
        if (released)
        {
            motion.Gliding = motion.Velocity.Length >= MouseGlideStopSpeed;
        }
        if (!motion.Gliding)
        {
            motion.Velocity = Axis2.Zero;
            motion.LastUpdate = timestamp;
            return;
        }

        ApplyMouseGlide(timestamp, ref desktop, ref motion);
    }

    private static void ApplyMouseGlide(
        DateTimeOffset timestamp,
        ref DesktopState desktop,
        ref PadMotion motion)
    {
        if (!motion.Gliding)
        {
            return;
        }

        var elapsed = ElapsedSeconds(motion.LastUpdate, timestamp);
        if (elapsed <= 0)
        {
            return;
        }

        desktop = desktop with
        {
            MouseX = desktop.MouseX + (motion.Velocity.X * elapsed),
            MouseY = desktop.MouseY + (motion.Velocity.Y * elapsed)
        };
        var decay = MathF.Exp(-MouseGlideFriction * elapsed);
        motion.Velocity = new Axis2(
            motion.Velocity.X * decay,
            motion.Velocity.Y * decay);
        if (motion.Velocity.Length < MouseGlideStopSpeed)
        {
            motion.Velocity = Axis2.Zero;
            motion.Gliding = false;
        }
        motion.LastUpdate = timestamp;
    }

    private static float ElapsedSeconds(DateTimeOffset previous, DateTimeOffset current)
    {
        if (previous == default || current <= previous)
        {
            return 0;
        }

        return Math.Clamp((float)(current - previous).TotalSeconds, 0.001f, 0.1f);
    }

    private static Axis2 ClampLength(Axis2 value, float maximum)
    {
        var length = value.Length;
        if (length <= maximum || length == 0)
        {
            return value;
        }

        var scale = maximum / length;
        return new Axis2(value.X * scale, value.Y * scale);
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

    private struct PadMotion
    {
        public Axis2 LastPosition;
        public Axis2 Velocity;
        public DateTimeOffset LastUpdate;
        public bool HadTouch;
        public bool Gliding;
    }
}
