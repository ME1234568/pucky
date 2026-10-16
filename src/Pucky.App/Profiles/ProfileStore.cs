using System.Text.Json;
using System.Text.Json.Serialization;
using Pucky.Core.Input;
using Pucky.Core.Mapping;

namespace Pucky.App.Profiles;

public sealed class ProfileValidationException(string message) : Exception(message);

/// <summary>
/// A profile file with a problem. When <see cref="Loaded"/> is true the profile
/// still works but must be fixed before it can be saved; otherwise the file
/// could not be used at all.
/// </summary>
public sealed record ProfileProblem(string File, string? Id, string? Name, string Error, bool Loaded);

public sealed record ProfileListing(
    IReadOnlyList<MappingProfile> Profiles,
    IReadOnlyList<ProfileProblem> Problems);

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

        var activeId = _settings.Current.ActiveProfileId;
        Active = (IsValidId(activeId) ? (await ReadAsync(PathFor(activeId), cancellationToken)).Profile : null)
                 ?? (await ReadAsync(defaultPath, cancellationToken)).Profile
                 ?? MappingProfile.Default();
    }

    public async Task<ProfileListing> ListAsync(CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(_directory);
        var profiles = new List<MappingProfile>();
        var problems = new List<ProfileProblem>();
        foreach (var path in Directory.EnumerateFiles(_directory, "*.json"))
        {
            var (profile, problem) = await ReadAsync(path, cancellationToken);
            if (profile is not null)
            {
                profiles.Add(profile);
            }
            if (problem is not null)
            {
                problems.Add(problem);
            }
        }
        return new ProfileListing(
            profiles
                .OrderBy(profile => profile.Id != DefaultProfileId)
                .ThenBy(profile => profile.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToArray(),
            problems.OrderBy(problem => problem.File, StringComparer.OrdinalIgnoreCase).ToArray());
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

        var (profile, _) = await ReadAsync(PathFor(id), cancellationToken);
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
        Layers = (profile.Layers ?? [])
            .Where(layer => layer is not null)
            .Select(layer => layer with
            {
                ButtonMappings = layer.ButtonMappings ?? [],
                KeyMappings = RemoveEmptyKeyBindings(layer.KeyMappings)
            })
            .ToList()
    };

    private static PadSettings NormalizePad(PadSettings pad) => pad with
    {
        Sensitivity = Math.Clamp(pad.Sensitivity, 0, 50),
        Deadzone = Math.Clamp(pad.Deadzone, 0, 0.95f),
        Acceleration = Math.Clamp(pad.Acceleration, 0, 2),
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

    /// <summary>
    /// Reads a profile leniently. Only files that cannot be parsed or are
    /// missing required sections are rejected; anything else that fails
    /// validation still loads, with a problem the UI shows, and has to be
    /// fixed before it can be saved again.
    /// </summary>
    private async Task<(MappingProfile? Profile, ProfileProblem? Problem)> ReadAsync(
        string path,
        CancellationToken cancellationToken)
    {
        var file = Path.GetFileName(path);
        var fileId = Path.GetFileNameWithoutExtension(path);
        try
        {
            if (!File.Exists(path))
            {
                return (null, null);
            }
            if (!IsValidId(fileId))
            {
                return (null, new ProfileProblem(file, null, null,
                    "File name must be lowercase letters, numbers, dashes, or underscores.", false));
            }

            MappingProfile? profile;
            await using (var stream = File.OpenRead(path))
            {
                profile = await JsonSerializer.DeserializeAsync<MappingProfile>(
                    stream,
                    _json,
                    cancellationToken);
            }
            if (profile is null)
            {
                return (null, new ProfileProblem(file, fileId, null, "The file is empty.", false));
            }
            if (profile.ButtonMappings is null || profile.LeftPad is null ||
                profile.RightPad is null || profile.Gyro is null)
            {
                return (null, new ProfileProblem(file, fileId, profile.Name,
                    "Profile is missing required sections.", false));
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
            profile = Normalize(profile with { Id = fileId });
            if (string.IsNullOrWhiteSpace(profile.Name))
            {
                profile = profile with { Name = fileId };
            }

            try
            {
                Validate(profile);
                return (profile, null);
            }
            catch (ProfileValidationException ex)
            {
                return (profile, new ProfileProblem(file, fileId, profile.Name, ex.Message, true));
            }
        }
        catch (JsonException ex)
        {
            var where = ex.LineNumber is { } line ? $" near line {line + 1}" : "";
            return (null, new ProfileProblem(file, fileId, null,
                $"The file is not valid profile JSON{where}.", false));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return (null, new ProfileProblem(file, fileId, null,
                $"The file could not be read ({ex.Message}).", false));
        }
    }
}
