using System.IO;
using System.Text.Json;

namespace BasitBilgisayarAraclari.App.Services.FileOperations;

public sealed record GameProfile(Guid Id, string Name, string TargetDirectory, bool SkipSingleTopFolder);

public sealed record RecentFileOperationPaths(IReadOnlyList<string> Sources, string? TargetDirectory);

public sealed class GameProfileStore
{
    private const int RecentPathLimit = 12;
    private readonly string _settingsPath;
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public GameProfileStore(string? applicationDataRoot = null)
    {
        var root = applicationDataRoot ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "BasitBilgisayarAraclari");
        _settingsPath = Path.Combine(root, "file-operation-settings.json");
    }

    public IReadOnlyList<GameProfile> GetProfiles() => Load().Profiles
        .OrderBy(profile => profile.Name, StringComparer.CurrentCultureIgnoreCase)
        .ToArray();

    public void SaveProfile(string name, string targetDirectory, bool skipSingleTopFolder, Guid? id = null)
    {
        var normalizedName = name.Trim();
        if (normalizedName.Length is < 1 or > 80)
        {
            throw new ArgumentException("Profil adı 1–80 karakter olmalı.", nameof(name));
        }

        if (string.IsNullOrWhiteSpace(targetDirectory))
        {
            throw new ArgumentException("Oyun klasörünü seç.", nameof(targetDirectory));
        }

        var settings = Load();
        var profileId = id ?? settings.Profiles.FirstOrDefault(profile => string.Equals(profile.Name, normalizedName, StringComparison.CurrentCultureIgnoreCase))?.Id ?? Guid.NewGuid();
        var profile = new GameProfile(profileId, normalizedName, Path.GetFullPath(targetDirectory), skipSingleTopFolder);
        var profiles = settings.Profiles.Where(existing => existing.Id != profileId && !string.Equals(existing.Name, normalizedName, StringComparison.CurrentCultureIgnoreCase)).Append(profile).ToArray();
        Save(settings with { Profiles = profiles });
    }

    public void DeleteProfile(Guid id)
    {
        var settings = Load();
        Save(settings with { Profiles = settings.Profiles.Where(profile => profile.Id != id).ToArray() });
    }

    public RecentFileOperationPaths GetRecentPaths()
    {
        var settings = Load();
        return new RecentFileOperationPaths(settings.RecentSources, settings.RecentTargetDirectory);
    }

    public void RememberPaths(IEnumerable<string> sources, string? targetDirectory)
    {
        var settings = Load();
        var normalized = sources
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(RecentPathLimit)
            .ToArray();
        Save(settings with
        {
            RecentSources = normalized,
            RecentTargetDirectory = string.IsNullOrWhiteSpace(targetDirectory) ? settings.RecentTargetDirectory : Path.GetFullPath(targetDirectory)
        });
    }

    private FileOperationSettings Load()
    {
        if (!File.Exists(_settingsPath))
        {
            return new FileOperationSettings([], [], null);
        }

        try
        {
            return JsonSerializer.Deserialize<FileOperationSettings>(File.ReadAllText(_settingsPath), _jsonOptions)
                ?? new FileOperationSettings([], [], null);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return new FileOperationSettings([], [], null);
        }
    }

    private void Save(FileOperationSettings settings)
    {
        var directory = Path.GetDirectoryName(_settingsPath)!;
        Directory.CreateDirectory(directory);
        var temporary = _settingsPath + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(settings, _jsonOptions));
        File.Move(temporary, _settingsPath, overwrite: true);
    }

    private sealed record FileOperationSettings(
        IReadOnlyList<GameProfile> Profiles,
        IReadOnlyList<string> RecentSources,
        string? RecentTargetDirectory);
}
