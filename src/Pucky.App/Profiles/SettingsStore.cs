using System.Text.Json;
using System.Text.Json.Serialization;

namespace Pucky.App.Profiles;

public enum GamepadOutputType
{
    /// <summary>Xbox 360 on Windows; Xbox-compatible uinput on Linux; Serval HID on macOS.</summary>
    Xbox360,
    /// <summary>DualShock 4 with gyro/accelerometer passthrough (Windows ViGEmBus).</summary>
    DualShock4
}

public sealed record AppSettings
{
    public GamepadOutputType OutputType { get; init; } = GamepadOutputType.Xbox360;
    public string ActiveProfileId { get; init; } = "default";

    /// <summary>
    /// Release the controller (restoring its built-in lizard mode and removing
    /// the virtual gamepad) while any of <see cref="HandOffProcesses"/> runs,
    /// so Steam Input or a game can use the controller directly.
    /// </summary>
    public bool HandOffEnabled { get; init; }
    public List<string> HandOffProcesses { get; init; } = DefaultHandOffProcesses();

    /// <summary>Do not open the control window automatically at startup.</summary>
    public bool StartInBackground { get; init; }

    public static List<string> DefaultHandOffProcesses() =>
        OperatingSystem.IsMacOS() ? ["steam_osx"] : ["steam"];
}

public sealed class SettingsStore
{
    private readonly string _path;
    private readonly JsonSerializerOptions _json;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ILogger<SettingsStore> _logger;

    public SettingsStore(IHostEnvironment environment, ILogger<SettingsStore> logger)
    {
        _logger = logger;
        _path = Path.Combine(environment.ContentRootPath, "data", "settings.json");
        _json = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true,
            Converters = { new JsonStringEnumConverter() }
        };
        Current = Load();
    }

    public AppSettings Current { get; private set; }

    public event Action<AppSettings>? Changed;

    public async Task<AppSettings> UpdateAsync(
        Func<AppSettings, AppSettings> update,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        AppSettings updated;
        try
        {
            updated = Normalize(update(Current));
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var temporaryPath = _path + ".tmp";
            await File.WriteAllTextAsync(
                temporaryPath,
                JsonSerializer.Serialize(updated, _json),
                cancellationToken);
            File.Move(temporaryPath, _path, overwrite: true);
            Current = updated;
        }
        finally
        {
            _gate.Release();
        }

        Changed?.Invoke(updated);
        return updated;
    }

    private AppSettings Load()
    {
        try
        {
            if (File.Exists(_path))
            {
                var settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(_path), _json);
                if (settings is not null)
                {
                    return Normalize(settings);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not read {Path}; using default settings", _path);
        }

        return new AppSettings();
    }

    private static AppSettings Normalize(AppSettings settings)
    {
        var processes = (settings.HandOffProcesses ?? [])
            .Select(NormalizeProcessName)
            .Where(name => name.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(32)
            .ToList();
        return settings with
        {
            ActiveProfileId = string.IsNullOrWhiteSpace(settings.ActiveProfileId)
                ? "default"
                : settings.ActiveProfileId,
            HandOffProcesses = processes,
            OutputType = Enum.IsDefined(settings.OutputType)
                ? settings.OutputType
                : GamepadOutputType.Xbox360
        };
    }

    /// <summary>Accepts "Steam", "steam.exe", or a full path and returns "steam".</summary>
    public static string NormalizeProcessName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return "";
        }

        var trimmed = Path.GetFileName(name.Trim().Replace('\\', '/'));
        if (trimmed.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = trimmed[..^4];
        }
        return trimmed.Length > 64 ? trimmed[..64] : trimmed;
    }
}
