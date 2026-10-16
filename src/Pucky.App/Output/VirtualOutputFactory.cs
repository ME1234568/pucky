using Pucky.App.Profiles;

namespace Pucky.App.Output;

public sealed class VirtualOutputFactory(IServiceProvider services)
{
    public static bool Supports(GamepadOutputType type) => type switch
    {
        GamepadOutputType.Xbox360 => true,
        GamepadOutputType.DualShock4 => OperatingSystem.IsWindows(),
        _ => false
    };

    public static IReadOnlyList<GamepadOutputType> SupportedTypes() =>
        Enum.GetValues<GamepadOutputType>().Where(Supports).ToArray();

    public IVirtualOutput Create(GamepadOutputType type = GamepadOutputType.Xbox360)
    {
        if (!Supports(type))
        {
            type = GamepadOutputType.Xbox360;
        }

        if (OperatingSystem.IsWindows())
        {
            return type == GamepadOutputType.DualShock4
                ? ActivatorUtilities.CreateInstance<WindowsDualShock4Output>(services)
                : ActivatorUtilities.CreateInstance<WindowsVigemOutput>(services);
        }

        if (OperatingSystem.IsLinux())
        {
            return ActivatorUtilities.CreateInstance<LinuxUInputOutput>(services);
        }

        if (OperatingSystem.IsMacOS())
        {
            return ActivatorUtilities.CreateInstance<MacHidGamepadOutput>(services);
        }

        return new UnavailableOutput(
            "A virtual gamepad backend is unavailable on this platform.");
    }
}
