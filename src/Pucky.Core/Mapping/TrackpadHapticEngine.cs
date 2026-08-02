using Pucky.Core.Input;

namespace Pucky.Core.Mapping;

public readonly record struct TrackpadHapticPulse(
    TrackpadHapticSide Side,
    ushort OnMicroseconds,
    ushort OffMicroseconds,
    ushort RepeatCount);

public sealed class TrackpadHapticEngine
{
    private const float TickSpacing = 0.055f;
    private const float OutputStrength = 0.85f;
    private PadMotion _left;
    private PadMotion _right;

    public IReadOnlyList<TrackpadHapticPulse> Update(
        ControllerState state,
        MappingProfile profile)
    {
        var pulses = new List<TrackpadHapticPulse>(2);
        UpdatePad(
            state.LeftPad,
            profile.LeftPad,
            TrackpadHapticSide.Left,
            ref _left,
            pulses);
        UpdatePad(
            state.RightPad,
            profile.RightPad,
            TrackpadHapticSide.Right,
            ref _right,
            pulses);
        return pulses;
    }

    public void Reset()
    {
        _left = default;
        _right = default;
    }

    private static void UpdatePad(
        TrackpadState pad,
        PadSettings settings,
        TrackpadHapticSide side,
        ref PadMotion motion,
        List<TrackpadHapticPulse> pulses)
    {
        var intensity = Math.Clamp(settings.HapticIntensity, 0f, 1f);
        if (!settings.HapticsEnabled || intensity <= 0)
        {
            motion = default;
            return;
        }

        if (pad.Clicked && !motion.Clicked)
        {
            pulses.Add(new TrackpadHapticPulse(
                side,
                ScalePulse(2200, intensity),
                0,
                1));
            motion.AccumulatedDistance = 0;
        }
        else if (pad.Touched && motion.Touched)
        {
            var deltaX = pad.Position.X - motion.Position.X;
            var deltaY = pad.Position.Y - motion.Position.Y;
            motion.AccumulatedDistance += MathF.Sqrt((deltaX * deltaX) + (deltaY * deltaY));
            if (motion.AccumulatedDistance >= TickSpacing)
            {
                var tickCount = Math.Clamp(
                    (int)(motion.AccumulatedDistance / TickSpacing),
                    1,
                    4);
                pulses.Add(new TrackpadHapticPulse(
                    side,
                    ScalePulse(720, intensity),
                    650,
                    (ushort)tickCount));
                motion.AccumulatedDistance -= tickCount * TickSpacing;
            }
        }

        if (!pad.Touched)
        {
            motion.AccumulatedDistance = 0;
        }

        motion.Position = pad.Position;
        motion.Touched = pad.Touched;
        motion.Clicked = pad.Clicked;
    }

    private static ushort ScalePulse(int maximum, float intensity) =>
        (ushort)Math.Round(maximum * intensity * OutputStrength);

    private struct PadMotion
    {
        public Axis2 Position;
        public float AccumulatedDistance;
        public bool Touched;
        public bool Clicked;
    }
}
