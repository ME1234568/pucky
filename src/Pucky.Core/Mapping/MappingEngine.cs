using Pucky.Core.Input;

namespace Pucky.Core.Mapping;

public sealed record VirtualGamepadState(
    VirtualButton Buttons,
    Axis2 LeftStick,
    Axis2 RightStick,
    float LeftTrigger,
    float RightTrigger,
    ImuState? Motion = null)
{
    public static readonly VirtualGamepadState Neutral =
        new(VirtualButton.None, Axis2.Zero, Axis2.Zero, 0, 0);
}

public sealed record DesktopState(
    float MouseX,
    float MouseY,
    float ScrollX,
    float ScrollY,
    bool LeftClick,
    bool RightClick,
    KeySet Keys = default)
{
    public static readonly DesktopState Idle = new(0, 0, 0, 0, false, false);

    /// <summary>
    /// Held keys including pad clicks, so outputs can treat every key and
    /// mouse button uniformly.
    /// </summary>
    public KeySet HeldKeys
    {
        get
        {
            var keys = Keys;
            if (LeftClick) keys = keys.With(KeyboardKey.MouseLeft);
            if (RightClick) keys = keys.With(KeyboardKey.MouseRight);
            return keys;
        }
    }
}

public sealed record MappingResult(VirtualGamepadState Gamepad, DesktopState Desktop);

public sealed class MappingEngine
{
    private const float MouseScale = 36f;
    private const float MouseVelocityBlend = 0.65f;
    private const float MouseGlideFriction = 8f;
    private const float MouseGlideStopSpeed = 5f;
    private const float MouseMaxGlideSpeed = 5000f;
    // Pointer acceleration, in pad units per second (the pad is 2 units
    // wide). Speed is smoothed over ~30 ms so random jitter from a resting
    // finger averages out instead of reading as movement.
    private const float AccelerationSmoothingSeconds = 0.03f;
    private const float AccelerationSlowSpeed = 0.4f;
    private const float AccelerationFastSpeed = 4f;
    private const float AccelerationSlowGain = 0.35f;
    private const float AccelerationFastGain = 3f;
    private const float AccelerationMinGain = 0.05f;
    // Triton gyro full scale is +/-2000 degrees per second.
    private const float GyroFullScaleDegrees = 2000f;
    // Pixels per degree at 1x sensitivity; matches the original per-report
    // feel at the controller's ~248 Hz report rate.
    private const float GyroMousePixelsPerDegree = 1.5f;
    private PadMotion _leftPadMotion;
    private PadMotion _rightPadMotion;
    private DateTimeOffset _lastGyroUpdate;

    public MappingResult Map(ControllerState input, MappingProfile profile)
    {
        var (buttons, keys) = MapButtons(input, profile);
        var leftStick = ApplyRadialDeadzone(input.LeftStick, profile.LeftStickDeadzone);
        var rightStick = ApplyRadialDeadzone(input.RightStick, profile.RightStickDeadzone);
        var desktop = DesktopState.Idle with { Keys = keys };

        // Each trackpad's joystick mode drives the stick on its own side.
        ApplyPad(
            input.LeftPad,
            profile.LeftPad,
            ref leftStick,
            ref buttons,
            ref desktop,
            input.ReceivedAt,
            ref _leftPadMotion);

        ApplyPad(
            input.RightPad,
            profile.RightPad,
            ref rightStick,
            ref buttons,
            ref desktop,
            input.ReceivedAt,
            ref _rightPadMotion);

        ApplyGyro(input, profile.Gyro, ref rightStick, ref desktop, ref _lastGyroUpdate);

        return new MappingResult(
            new VirtualGamepadState(
                buttons,
                ClampAxis(leftStick),
                ClampAxis(rightStick),
                TriggerValue(input, SteamButton.LeftTriggerFull, input.LeftTrigger),
                TriggerValue(input, SteamButton.RightTriggerFull, input.RightTrigger),
                input.Imu),
            desktop);
    }

    public DesktopState ContinueDesktopMotion(
        DateTimeOffset timestamp,
        MappingProfile profile,
        KeySet heldKeys = default)
    {
        var desktop = DesktopState.Idle with { Keys = heldKeys };
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
        _lastGyroUpdate = default;
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

    /// <summary>
    /// Resolves gamepad and keyboard outputs. A held layer replaces both the
    /// gamepad and keyboard binding of every source it mentions; later layers
    /// win over earlier ones. Runs without per-report allocations.
    /// </summary>
    private static (VirtualButton Buttons, KeySet Keys) MapButtons(
        ControllerState input,
        MappingProfile profile)
    {
        var output = VirtualButton.None;
        var keys = KeySet.Empty;
        var claimed = SteamButton.None;

        for (var index = profile.Layers.Count - 1; index >= 0; index--)
        {
            var layer = profile.Layers[index];
            if (layer.HoldButton == SteamButton.None || !input.IsPressed(layer.HoldButton))
            {
                continue;
            }

            var layerSources = SteamButton.None;
            foreach (var (source, target) in layer.ButtonMappings)
            {
                layerSources |= source;
                if ((claimed & source) == 0 && input.IsPressed(source))
                {
                    output |= target;
                }
            }
            if (layer.KeyMappings is not null)
            {
                foreach (var (source, targets) in layer.KeyMappings)
                {
                    layerSources |= source;
                    if ((claimed & source) == 0 && input.IsPressed(source))
                    {
                        keys = AddKeys(keys, targets);
                    }
                }
            }
            claimed |= layerSources;
        }

        foreach (var (source, target) in profile.ButtonMappings)
        {
            if ((claimed & source) == 0 && input.IsPressed(source))
            {
                output |= target;
            }
        }
        if (profile.KeyMappings is not null)
        {
            foreach (var (source, targets) in profile.KeyMappings)
            {
                if ((claimed & source) == 0 && input.IsPressed(source))
                {
                    keys = AddKeys(keys, targets);
                }
            }
        }

        return (output, keys);
    }

    private static KeySet AddKeys(KeySet keys, List<KeyboardKey>? targets)
    {
        if (targets is null)
        {
            return keys;
        }

        foreach (var key in targets)
        {
            keys = keys.With(key);
        }
        return keys;
    }

    private static void ApplyPad(
        TrackpadState pad,
        PadSettings settings,
        ref Axis2 stick,
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
                stick = value;
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
                    ScrollY = desktop.ScrollY - (delta.Y * settings.Sensitivity * 8f)
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
            var gain = settings.Sensitivity * MouseScale *
                       AccelerationGain(delta, elapsed, settings.Acceleration, ref motion);
            var movement = new Axis2(delta.X * gain, -delta.Y * gain);

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
        motion.PadVelocity = Axis2.Zero;
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

    /// <summary>
    /// Scales pad movement by finger speed: below the slow speed movement is
    /// damped, above the fast speed it is amplified, with a smooth curve in
    /// between. <paramref name="acceleration"/> blends from linear (0) to the
    /// full curve (1) and beyond.
    /// </summary>
    private static float AccelerationGain(
        Axis2 delta,
        float elapsed,
        float acceleration,
        ref PadMotion motion)
    {
        if (!motion.HadTouch || elapsed <= 0)
        {
            // First contact: there is no movement to scale yet.
            motion.PadVelocity = Axis2.Zero;
            return 1f;
        }

        var instantaneous = new Axis2(delta.X / elapsed, delta.Y / elapsed);
        var blend = 1 - MathF.Exp(-elapsed / AccelerationSmoothingSeconds);
        motion.PadVelocity = new Axis2(
            motion.PadVelocity.X + ((instantaneous.X - motion.PadVelocity.X) * blend),
            motion.PadVelocity.Y + ((instantaneous.Y - motion.PadVelocity.Y) * blend));
        if (acceleration <= 0)
        {
            return 1f;
        }

        var t = Math.Clamp(
            (motion.PadVelocity.Length - AccelerationSlowSpeed) /
            (AccelerationFastSpeed - AccelerationSlowSpeed),
            0f,
            1f);
        var curve = AccelerationSlowGain +
                    ((AccelerationFastGain - AccelerationSlowGain) * t * t * (3 - (2 * t)));
        return Math.Max(AccelerationMinGain, 1 + ((curve - 1) * acceleration));
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

    /// <summary>
    /// Converts Triton IMU axes to screen motion. The controller reports
    /// gravity on Z when lying flat, so yaw is rotation about Z, pitch about X,
    /// and roll about Y (matching SDL's Triton sensor mapping).
    /// </summary>
    public static Axis2 GyroToScreen(ImuState imu, GyroSettings settings)
    {
        var horizontal = settings.HorizontalAxis == GyroHorizontalAxis.Roll
            ? imu.GyroscopeXY.Y
            : -imu.GyroscopeZ;
        var vertical = -imu.GyroscopeXY.X;
        return new Axis2(
            settings.InvertX ? -horizontal : horizontal,
            settings.InvertY ? -vertical : vertical);
    }

    private static void ApplyGyro(
        ControllerState input,
        GyroSettings settings,
        ref Axis2 rightStick,
        ref DesktopState desktop,
        ref DateTimeOffset lastUpdate)
    {
        var previous = lastUpdate;
        lastUpdate = input.ReceivedAt;
        if (settings.Mode == GyroMode.Disabled ||
            input.Imu is null ||
            (settings.EnableButton != SteamButton.None && !input.IsPressed(settings.EnableButton)))
        {
            return;
        }

        var gyro = ApplyRadialDeadzone(
            GyroToScreen(input.Imu.Value, settings),
            settings.Deadzone);

        if (settings.Mode == GyroMode.RightStick)
        {
            rightStick = ClampAxis(new Axis2(
                rightStick.X + (gyro.X * settings.Sensitivity),
                rightStick.Y - (gyro.Y * settings.Sensitivity)));
        }
        else if (settings.Mode == GyroMode.Mouse)
        {
            var elapsed = ElapsedSeconds(previous, input.ReceivedAt);
            if (elapsed <= 0)
            {
                return;
            }

            var scale = GyroFullScaleDegrees * elapsed * settings.Sensitivity *
                        GyroMousePixelsPerDegree;
            desktop = desktop with
            {
                MouseX = desktop.MouseX + (gyro.X * scale),
                MouseY = desktop.MouseY + (gyro.Y * scale)
            };
        }
    }

    private static Axis2 ClampAxis(Axis2 value) =>
        new(Math.Clamp(value.X, -1, 1), Math.Clamp(value.Y, -1, 1));

    private static float TriggerValue(
        ControllerState input,
        SteamButton fullPressButton,
        float analogValue) =>
        input.IsPressed(fullPressButton) ? 1f : Math.Clamp(analogValue, 0, 1);

    private struct PadMotion
    {
        public Axis2 LastPosition;
        public Axis2 Velocity;
        /// <summary>Smoothed finger velocity in pad units per second.</summary>
        public Axis2 PadVelocity;
        public DateTimeOffset LastUpdate;
        public bool HadTouch;
        public bool Gliding;
    }
}
