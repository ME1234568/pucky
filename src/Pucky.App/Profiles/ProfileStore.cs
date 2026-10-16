using System.Text.Json;
using System.Text.Json.Serialization;
using Pucky.Core.Input;
using Pucky.Core.Mapping;

namespace Pucky.App.Profiles;

public sealed class ProfileValidationException(string message) : Exception(message);

public sealed class ProfileStore
{
    public const string DefaultProfileId = "default";
    private const int MaxKeysPerBinding = 6;
    private const int MaxLayers = 16;

    private const VirtualButton ValidTargets =
        VirtualButton.A | VirtualButton.B | VirtualButton.X | VirtualButton.Y |
        VirtualButton.LeftBumper | VirtualButton.RightBumper |
        VirtualButton.Back | VirtualButton.Start | VirtualButton.Guide |
        VirtualButton.LeftStick | VirtualButton.RightStick |
        VirtualButton.DPadUp | VirtualButton.DPadDown |
        VirtualButton.DPadLeft | VirtualButton.DPadRight |
        VirtualButton.QuickAccess;

    private readonly string _directory;
    private readonly JsonSerializerOptions _json;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly SettingsStore _settings;

    public ProfileStore(IHostEnvironment environment, SettingsStore settings)
    {
        _settings = settings;
        _directory = Path.Combine(environment.ContentRootPath, "data", "profiles");
        _json = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true,
            Converters = { new JsonStringEnumConverter() }
        };
    }

    public MappingProfile Active { get; private set; } = MappingProfile.Default();

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(_directory);
        var defaultPath = PathFor(DefaultProfileId);
        if (!File.Exists(defaultPath))
        {
            await WriteAsync(MappingProfile.Default(), cancellationToken);
        }

        Active = await ReadAsync(PathFor(_settings.Current.ActiveProfileId), cancellationToken)
                 ?? await ReadAsync(defaultPath, cancellationToken)
                 ?? MappingProfile.Default();
    }

    public async Task<IReadOnlyList<MappingProfile>> ListAsync(
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(_directory);
        var profiles = new List<MappingProfile>();
        foreach (var path in Directory.EnumerateFiles(_directory, "*.json"))
        {
            var profile = await ReadAsync(path, cancellationToken);
            if (profile is not null)
            {
                profiles.Add(profile);
            }
        }
        return profiles
            .OrderBy(profile => profile.Id != DefaultProfileId)
            .ThenBy(profile => profile.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
    }

    /// <summary>Validates, writes, and activates a profile.</summary>
    public async Task<MappingProfile> SaveAsync(
        MappingProfile profile,
        CancellationToken cancellationToken = default)
    {
        profile = Normalize(profile);
        Validate(profile);
        await WriteAsync(profile, cancellationToken);
        await SetActiveAsync(profile, cancellationToken);
        return profile;
    }

    public async Task<MappingProfile?> ActivateAsync(
        string id,
        CancellationToken cancellationToken = default)
    {
        if (!IsValidId(id))
        {
            return null;
        }

        var profile = await ReadAsync(PathFor(id), cancellationToken);
        if (profile is not null)
        {
            await SetActiveAsync(profile, cancellationToken);
        }
        return profile;
    }

    /// <summary>Deletes a profile. The default profile cannot be deleted.</summary>
    public async Task<bool> DeleteAsync(string id, CancellationToken cancellationToken = default)
    {
        if (!IsValidId(id) || id == DefaultProfileId)
        {
            return false;
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            var path = PathFor(id);
            if (!File.Exists(path))
            {
                return false;
            }
            File.Delete(path);
        }
        finally
        {
            _gate.Release();
        }

        if (Active.Id == id)
        {
            await ActivateAsync(DefaultProfileId, cancellationToken);
        }
        return true;
    }

    public static bool IsValidId(string? id) =>
        !string.IsNullOrEmpty(id) &&
        id.Length <= 64 &&
        id.All(character => character is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '-' or '_');

    /// <summary>Builds a filesystem-safe id from a display name.</summary>
    public static string SlugFromName(string name)
    {
        var slug = new string(name
            .ToLowerInvariant()
            .Select(character => character is (>= 'a' and <= 'z') or (>= '0' and <= '9') ? character : '-')
            .ToArray());
        while (slug.Contains("--", StringComparison.Ordinal))
        {
            slug = slug.Replace("--", "-", StringComparison.Ordinal);
        }
        slug = slug.Trim('-');
        if (slug.Length > 48)
        {
            slug = slug[..48].TrimEnd('-');
        }
        return slug.Length == 0 ? "profile" : slug;
    }

    public static void Validate(MappingProfile profile)
    {
        if (!IsValidId(profile.Id))
        {
            throw new ProfileValidationException(
                "Profile id must be 1-64 lowercase letters, numbers, dashes, or underscores.");
        }
        if (string.IsNullOrWhiteSpace(profile.Name) || profile.Name.Length > 80)
        {
            throw new ProfileValidationException("Profile name must be 1-80 characters.");
        }
        if (profile.ButtonMappings is null || profile.LeftPad is null ||
            profile.RightPad is null || profile.Gyro is null)
        {
            throw new ProfileValidationException("Profile is missing required sections.");
        }
        if (profile.Layers.Count > MaxLayers)
        {
            throw new ProfileValidationException($"A profile may have at most {MaxLayers} layers.");
        }

        ValidateBindings(profile.ButtonMappings, profile.KeyMappings, "profile");
        foreach (var layer in profile.Layers)
        {
            if (layer is null || layer.ButtonMappings is null)
            {
                throw new ProfileValidationException("Layers need button mappings.");
            }
            if (!IsSingleSource(layer.HoldButton))
            {
                throw new ProfileValidationException(
                    $"Layer '{layer.Name}' needs exactly one hold button.");
            }
            ValidateBindings(layer.ButtonMappings, layer.KeyMappings, $"layer '{layer.Name}'");
        }
        if (profile.Gyro.EnableButton != SteamButton.None && !IsSingleSource(profile.Gyro.EnableButton))
        {
            throw new ProfileValidationException("Gyro enable button must be a single button.");
        }
    }

    private static void ValidateBindings(
        Dictionary<SteamButton, VirtualButton> buttons,
        Dictionary<SteamButton, List<KeyboardKey>>? keys,
        string owner)
    {
        foreach (var (source, target) in buttons)
        {
            if (!IsSingleSource(source))
            {
                throw new ProfileValidationException($"Unknown source button '{source}' in {owner}.");
            }
            if ((target & ~ValidTargets) != 0)
            {
                throw new ProfileValidationException(
                    $"Unknown output button in mapping for '{source}' in {owner}.");
            }
        }

        foreach (var (source, targets) in keys ?? [])
        {
            if (!IsSingleSource(source))
            {
                throw new ProfileValidationException($"Unknown source button '{source}' in {owner}.");
            }
            if (targets is null || targets.Count > MaxKeysPerBinding ||
                targets.Any(key => key == KeyboardKey.None || !Enum.IsDefined(key)))
            {
                throw new ProfileValidationException(
                    $"Keyboard binding for '{source}' in {owner} must have 1-{MaxKeysPerBinding} known keys.");
            }
        }
    }

    private static bool IsSingleSource(SteamButton button) =>
        button != SteamButton.None &&
        Enum.IsDefined(button) &&
        ((ulong)button & ((ulong)button - 1)) == 0;

    private static MappingProfile Normalize(MappingProfile profile) => profile with
    {
        Name = profile.Name?.Trim() ?? "",
        LeftStickDeadzone = Math.Clamp(profile.LeftStickDeadzone, 0, 0.95f),
        RightStickDeadzone = Math.Clamp(profile.RightStickDeadzone, 0, 0.95f),
        LeftPad = profile.LeftPad is null ? null! : NormalizePad(profile.LeftPad),
        RightPad = profile.RightPad is null ? null! : NormalizePad(profile.RightPad),
        Gyro = profile.Gyro is null ? null! : profile.Gyro with
        {
            Sensitivity = Math.Clamp(profile.Gyro.Sensitivity, 0, 50),
            Deadzone = Math.Clamp(profile.Gyro.Deadzone, 0, 0.5f)
        },
        KeyMappings = RemoveEmptyKeyBindings(profile.KeyMappings),
        Layers = profile.Layers ?? []
    };

    private static PadSettings NormalizePad(PadSettings pad) => pad with
    {
        Sensitivity = Math.Clamp(pad.Sensitivity, 0, 50),
        Deadzone = Math.Clamp(pad.Deadzone, 0, 0.95f),
        HapticIntensity = Math.Clamp(pad.HapticIntensity, 0, 1)
    };

    private static Dictionary<SteamButton, List<KeyboardKey>> RemoveEmptyKeyBindings(
        Dictionary<SteamButton, List<KeyboardKey>>? keys) =>
        keys is null
            ? []
            : keys
                .Where(pair => pair.Value is { Count: > 0 })
                .ToDictionary(pair => pair.Key, pair => pair.Value.Distinct().ToList());

    private async Task SetActiveAsync(MappingProfile profile, CancellationToken cancellationToken)
    {
        Active = profile;
        if (_settings.Current.ActiveProfileId != profile.Id)
        {
            await _settings.UpdateAsync(
                settings => settings with { ActiveProfileId = profile.Id },
                cancellationToken);
        }
    }

    private async Task WriteAsync(MappingProfile profile, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            Directory.CreateDirectory(_directory);
            var path = PathFor(profile.Id);
            var temporaryPath = path + ".tmp";
            await File.WriteAllTextAsync(
                temporaryPath,
                JsonSerializer.Serialize(profile, _json),
                cancellationToken);
            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            _gate.Release();
        }
    }

    private string PathFor(string id) => Path.Combine(_directory, $"{id}.json");

    private async Task<MappingProfile?> ReadAsync(
        string path,
        CancellationToken cancellationToken)
    {
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            await using var stream = File.OpenRead(path);
            var profile = await JsonSerializer.DeserializeAsync<MappingProfile>(
                stream,
                _json,
                cancellationToken);
            if (profile is null)
            {
                return null;
            }

            // Profiles written before Quick Access existed get its default.
            // Newer profiles store an explicit "None" when it is unbound.
            if (profile.ButtonMappings is not null &&
                !profile.ButtonMappings.ContainsKey(SteamButton.QuickAccess))
            {
                profile = profile with
                {
                    ButtonMappings = new(profile.ButtonMappings)
                    {
                        [SteamButton.QuickAccess] = VirtualButton.QuickAccess
                    }
                };
            }

            // The file name is authoritative so a hand-edited id cannot
            // shadow another profile.
            var fileId = Path.GetFileNameWithoutExtension(path);
            if (profile.Id != fileId && IsValidId(fileId))
            {
                profile = profile with { Id = fileId };
            }

            profile = Normalize(profile);
            Validate(profile);
            return profile;
        }
        catch (Exception ex) when (ex is IOException or JsonException or
                                       UnauthorizedAccessException or ProfileValidationException)
        {
            return null;
        }
    }
}
