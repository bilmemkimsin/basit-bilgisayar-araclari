using System.IO;
using System.Text.Json;
using BasitBilgisayarAraclari.App.Localization;

namespace BasitBilgisayarAraclari.App.Services;

public sealed record UserPreferences(string LanguageCode = "tr", string Theme = "Dark");

public sealed class UserPreferencesStore
{
    private readonly string _settingsPath;
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public UserPreferencesStore(string? applicationDataRoot = null)
    {
        var root = applicationDataRoot ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "BasitBilgisayarAraclari");
        _settingsPath = Path.Combine(root, "user-preferences.json");
    }

    public UserPreferences Load()
    {
        try
        {
            if (!File.Exists(_settingsPath))
            {
                return new UserPreferences();
            }

            var preferences = JsonSerializer.Deserialize<UserPreferences>(File.ReadAllText(_settingsPath), _jsonOptions)
                ?? new UserPreferences();
            var language = LocalizationManager.Languages.Any(option =>
                string.Equals(option.Code, preferences.LanguageCode, StringComparison.OrdinalIgnoreCase))
                ? preferences.LanguageCode
                : "tr";
            var theme = string.Equals(preferences.Theme, "Light", StringComparison.OrdinalIgnoreCase)
                ? "Light"
                : "Dark";
            return new UserPreferences(language, theme);
        }
        catch (IOException)
        {
            return new UserPreferences();
        }
        catch (UnauthorizedAccessException)
        {
            return new UserPreferences();
        }
        catch (JsonException)
        {
            return new UserPreferences();
        }
    }

    public void Save(UserPreferences preferences)
    {
        ArgumentNullException.ThrowIfNull(preferences);
        Directory.CreateDirectory(Path.GetDirectoryName(_settingsPath)!);
        var temporaryPath = _settingsPath + ".tmp";
        File.WriteAllText(temporaryPath, JsonSerializer.Serialize(preferences, _jsonOptions));
        File.Move(temporaryPath, _settingsPath, overwrite: true);
    }
}
