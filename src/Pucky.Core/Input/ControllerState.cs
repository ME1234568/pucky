namespace Pucky.Core.Input;

public readonly record struct Axis2(float X, float Y)
{
    public static readonly Axis2 Zero = new(0, 0);

    public float Length => MathF.Sqrt((X * X) + (Y * Y));
}

public readonly record struct TrackpadState(Axis2 Position, float Contact, bool Touched, bool Clicked);

public enum TrackpadHapticSide : byte
{
    Left = 0x01,
    Right = 0x02
}

public readonly record struct ImuState(
    uint Timestamp,
    Axis2 AccelerometerXY,
    float AccelerometerZ,
    Axis2 GyroscopeXY,
    float GyroscopeZ);

public sealed record ControllerState
{
    public byte Sequence { get; init; }
    public SteamButton Buttons { get; init; }
    public float LeftTrigger { get; init; }
    public float RightTrigger { get; init; }
    public Axis2 LeftStick { get; init; }
    public Axis2 RightStick { get; init; }
    public TrackpadState LeftPad { get; init; }
    public TrackpadState RightPad { get; init; }
    public ImuState? Imu { get; init; }
    public DateTimeOffset ReceivedAt { get; init; } = DateTimeOffset.UtcNow;

    public bool IsPressed(SteamButton button) => (Buttons & button) == button;
}

public sealed record ControllerDeviceInfo(
    string Id,
    string Name,
    int VendorId,
    int ProductId,
    string Connection,
    int? BatteryPercent = null,
    bool? Charging = null);
