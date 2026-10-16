using System.Security;
using Microsoft.Win32;

namespace Pucky.App.Services;

/// <summary>
/// Registers Pucky to start in the background when the user logs in, using
/// each platform's per-user mechanism (no administrator rights required).
/// </summary>
public static class AutoStart
{
    private const string WindowsRunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string EntryName = "Pucky";
    private const string MacLabel = "io.github.pucky";

    /// <summary>
    /// Only a published executable can be registered; "dotnet run" goes
    /// through the shared dotnet host, which cannot be launched on its own.
    /// </summary>
    public static string? ExecutablePath
    {
        get
        {
            var path = Environment.ProcessPath;
            if (string.IsNullOrEmpty(path))
            {
                return null;
            }

            var name = Path.GetFileNameWithoutExtension(path);
            return name.Equals("dotnet", StringComparison.OrdinalIgnoreCase) ? null : path;
        }
    }

    public static bool IsSupported =>
        ExecutablePath is not null &&
        (OperatingSystem.IsWindows() || OperatingSystem.IsLinux() || OperatingSystem.IsMacOS());

    public static bool IsEnabled()
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                using var key = Registry.CurrentUser.OpenSubKey(WindowsRunKey);
                return key?.GetValue(EntryName) is string;
            }
            if (OperatingSystem.IsLinux())
            {
                return File.Exists(LinuxDesktopEntryPath());
            }
            if (OperatingSystem.IsMacOS())
            {
                return File.Exists(MacLaunchAgentPath());
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
        {
        }
        return false;
    }

    public static void SetEnabled(bool enabled)
    {
        var executable = ExecutablePath;
        if (enabled && executable is null)
        {
            throw new InvalidOperationException(
                "Launch at login is available from a published Pucky build, not from 'dotnet run'.");
        }

        if (OperatingSystem.IsWindows())
        {
            using var key = Registry.CurrentUser.CreateSubKey(WindowsRunKey);
            if (enabled)
            {
                key.SetValue(EntryName, $"\"{executable}\" --background");
            }
            else
            {
                key.DeleteValue(EntryName, throwOnMissingValue: false);
            }
        }
        else if (OperatingSystem.IsLinux())
        {
            var path = LinuxDesktopEntryPath();
            if (enabled)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, $"""
                    [Desktop Entry]
                    Type=Application
                    Name=Pucky
                    Comment=Steam Controller mapper
                    Exec="{executable}" --background
                    Terminal=false
                    X-GNOME-Autostart-enabled=true

                    """);
            }
            else if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        else if (OperatingSystem.IsMacOS())
        {
            var path = MacLaunchAgentPath();
            if (enabled)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, $"""
                    <?xml version="1.0" encoding="UTF-8"?>
                    <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
                    <plist version="1.0">
                    <dict>
                      <key>Label</key><string>{MacLabel}</string>
                      <key>ProgramArguments</key>
                      <array>
                        <string>{SecurityElement.Escape(executable)}</string>
                        <string>--background</string>
                      </array>
                      <key>RunAtLoad</key><true/>
                    </dict>
                    </plist>

                    """);
            }
            else if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    private static string LinuxDesktopEntryPath()
    {
        var config = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        if (string.IsNullOrEmpty(config))
        {
            config = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".config");
        }
        return Path.Combine(config, "autostart", "pucky.desktop");
    }

    private static string MacLaunchAgentPath() =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Library",
            "LaunchAgents",
            $"{MacLabel}.plist");
}
