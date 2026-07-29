using System.Text.Json;
using System.Text.Json.Serialization;
using Pucky.Core.Mapping;

namespace Pucky.App.Profiles;

public sealed class ProfileStore
{
    private readonly string _directory;
    private readonly JsonSerializerOptions _json;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public ProfileStore(IHostEnvironment environment)
    {
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
        var defaultPath = Path.Combine(_directory, "default.json");
        if (File.Exists(defaultPath))
        {
            Active = await ReadAsync(defaultPath, cancellationToken) ?? MappingProfile.Default();
        }
        else
        {
            await SaveAsync(MappingProfile.Default(), cancellationToken);
        }
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
        return profiles.OrderBy(profile => profile.Name).ToArray();
    }

    public async Task SaveAsync(
        MappingProfile profile,
        CancellationToken cancellationToken = default)
    {
        Validate(profile);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            Directory.CreateDirectory(_directory);
            var path = Path.Combine(_directory, $"{profile.Id}.json");
            var temporaryPath = path + ".tmp";
            await File.WriteAllTextAsync(
                temporaryPath,
                JsonSerializer.Serialize(profile, _json),
                cancellationToken);
            File.Move(temporaryPath, path, overwrite: true);
            Active = profile;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<MappingProfile?> ActivateAsync(
        string id,
        CancellationToken cancellationToken = default)
    {
        var safeId = SanitizeId(id);
        var profile = await ReadAsync(
            Path.Combine(_directory, $"{safeId}.json"),
            cancellationToken);
        if (profile is not null)
        {
            Active = profile;
        }
        return profile;
    }

    private async Task<MappingProfile?> ReadAsync(
        string path,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = File.OpenRead(path);
            return await JsonSerializer.DeserializeAsync<MappingProfile>(
                stream,
                _json,
                cancellationToken);
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            return null;
        }
    }

    private static void Validate(MappingProfile profile)
    {
        if (SanitizeId(profile.Id) != profile.Id)
        {
            throw new ArgumentException(
                "Profile id may contain only lowercase letters, numbers, dashes, and underscores.");
        }
        if (string.IsNullOrWhiteSpace(profile.Name))
        {
            throw new ArgumentException("Profile name is required.");
        }
    }

    private static string SanitizeId(string id)
    {
        var safe = new string(id
            .ToLowerInvariant()
            .Where(character => char.IsLetterOrDigit(character) ||
                                character is '-' or '_')
            .ToArray());
        return safe;
    }
}
