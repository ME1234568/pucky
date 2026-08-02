namespace Pucky.App.Output;

public sealed class VirtualOutputFactory(IServiceProvider services)
{
    public IVirtualOutput Create()
    {
        if (OperatingSystem.IsWindows())
        {
            return ActivatorUtilities.CreateInstance<WindowsVigemOutput>(services);
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
