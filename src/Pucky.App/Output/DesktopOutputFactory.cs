namespace Pucky.App.Output;

public static class DesktopOutputFactory
{
    public static IDesktopOutput Create() =>
        OperatingSystem.IsWindows()
            ? new WindowsDesktopOutput()
            : OperatingSystem.IsLinux()
                ? new LinuxDesktopOutput()
                : OperatingSystem.IsMacOS()
                    ? new MacDesktopOutput()
                    : new NoDesktopOutput("Desktop output is unavailable on this platform.");
}
