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

        return new UnavailableOutput(
            "macOS does not expose a public system-wide virtual gamepad API. " +
            "Raw input, profiles, gyro, touchpads, and diagnostics remain available.");
    }
}
