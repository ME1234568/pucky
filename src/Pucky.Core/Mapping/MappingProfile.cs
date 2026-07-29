using Pucky.Core.Input;

namespace Pucky.Core.Mapping;

public enum PadMode
{
    Disabled,
    Joystick,
    Mouse,
    DPad,
    Scroll
}

public enum GyroMode
{
    Disabled,
    RightStick,
    Mouse
}

[Flags]
public enum VirtualButton : uint
{
    None = 0,
    A = 1 << 0,
    B = 1 << 1,
    X = 1 << 2,
    Y = 1 << 3,
    LeftBumper = 1 << 4,
    RightBumper = 1 << 5,
    Back = 1 << 6,
    Start = 1 << 7,
    Guide = 1 << 8,
    LeftStick = 1 << 9,
    RightStick = 1 << 10,
    DPadUp = 1 << 11,
    DPadDown = 1 << 12,
    DPadLeft = 1 << 13,
    DPadRight = 1 << 14
}

public sealed record PadSettings
{
    public PadMode Mode { get; init; } = PadMode.Disabled;
    public float Sensitivity { get; init; } = 1f;
    public float Deadzone { get; init; } = 0.08f;
    public bool ClickRequired { get; init; }
    public bool HapticsEnabled { get; init; } = true;
    public float HapticIntensity { get; init; } = 0.65f;
}

public sealed record GyroSettings
{
    public GyroMode Mode { get; init; } = GyroMode.Disabled;
    public float Sensitivity { get; init; } = 1.8f;
    public float Deadzone { get; init; } = 0.02f;
    public SteamButton EnableButton { get; init; } = SteamButton.RightStickTouch;
}

public sealed record ActionLayer
{
    public string Name { get; init; } = "Layer";
    public SteamButton HoldButton { get; init; }
    public Dictionary<SteamButton, VirtualButton> ButtonMappings { get; init; } = [];
}

public sealed record MappingProfile
{
    public string Id { get; init; } = "default";
    public string Name { get; init; } = "Desktop Gamepad";
    public float LeftStickDeadzone { get; init; } = 0.12f;
    public float RightStickDeadzone { get; init; } = 0.12f;
    public PadSettings LeftPad { get; init; } = new() { Mode = PadMode.DPad };
    public PadSettings RightPad { get; init; } = new() { Mode = PadMode.Mouse, Sensitivity = 1.2f };
    public GyroSettings Gyro { get; init; } = new();
    public Dictionary<SteamButton, VirtualButton> ButtonMappings { get; init; } = DefaultButtons();
    public List<ActionLayer> Layers { get; init; } = [];

    public static MappingProfile Default() => new();

    public static Dictionary<SteamButton, VirtualButton> DefaultButtons() => new()
    {
        [SteamButton.A] = VirtualButton.A,
        [SteamButton.B] = VirtualButton.B,
        [SteamButton.X] = VirtualButton.X,
        [SteamButton.Y] = VirtualButton.Y,
        [SteamButton.LeftBumper] = VirtualButton.LeftBumper,
        [SteamButton.RightBumper] = VirtualButton.RightBumper,
        [SteamButton.View] = VirtualButton.Back,
        [SteamButton.Menu] = VirtualButton.Start,
        [SteamButton.Steam] = VirtualButton.Guide,
        [SteamButton.LeftStick] = VirtualButton.LeftStick,
        [SteamButton.RightStick] = VirtualButton.RightStick,
        [SteamButton.DPadUp] = VirtualButton.DPadUp,
        [SteamButton.DPadDown] = VirtualButton.DPadDown,
        [SteamButton.DPadLeft] = VirtualButton.DPadLeft,
        [SteamButton.DPadRight] = VirtualButton.DPadRight,
        [SteamButton.L4] = VirtualButton.LeftStick,
        [SteamButton.L5] = VirtualButton.Back,
        [SteamButton.R4] = VirtualButton.RightStick,
        [SteamButton.R5] = VirtualButton.Start
    };
}
