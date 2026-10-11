using System.IO;
using System.Collections.ObjectModel;
using BasitBilgisayarAraclari.App.Core;
using BasitBilgisayarAraclari.App.Localization;
using BasitBilgisayarAraclari.App.Services;
using System.Windows;
using WpfApplication = System.Windows.Application;
using WpfMessageBox = System.Windows.MessageBox;

namespace BasitBilgisayarAraclari.App.ViewModels;

public sealed class AppSettingsViewModel : ObservableObject
{
    private readonly UserPreferencesStore _store;
    private readonly ObservableCollection<LanguageChoiceViewModel> _languages;
    private string _selectedLanguageCode;
    private bool _isDarkTheme;
    private bool _isLanguagePickerOpen;

    public AppSettingsViewModel(UserPreferencesStore store, UserPreferences preferences)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        ArgumentNullException.ThrowIfNull(preferences);
        _selectedLanguageCode = preferences.LanguageCode;
        _isDarkTheme = string.Equals(preferences.Theme, "Dark", StringComparison.OrdinalIgnoreCase);
        _languages = new ObservableCollection<LanguageChoiceViewModel>(LocalizationManager.Languages.Select(language =>
            new LanguageChoiceViewModel(language.Code, language.DisplayName,
                string.Equals(language.Code, _selectedLanguageCode, StringComparison.OrdinalIgnoreCase))));
        SelectLanguageCommand = new RelayCommand(SelectLanguage);
    }

    public IReadOnlyList<LanguageChoiceViewModel> Languages => _languages;

    public RelayCommand SelectLanguageCommand { get; }

    public string SelectedLanguageDisplayName =>
        _languages.FirstOrDefault(language => language.IsSelected)?.DisplayName ?? _selectedLanguageCode;

    public string SelectedLanguageBadge => _selectedLanguageCode switch
    {
        "tr" => "TR",
        "en" => "EN",
        "de" => "DE",
        "fr" => "FR",
        "zh-Hans" => "中文",
        "ko" => "한",
        "ja" => "日",
        "es" => "ES",
        _ => "TR"
    };

    public bool IsLanguagePickerOpen
    {
        get => _isLanguagePickerOpen;
        set => SetProperty(ref _isLanguagePickerOpen, value);
    }

    public string SelectedLanguageCode
    {
        get => _selectedLanguageCode;
        set
        {
            if (SetProperty(ref _selectedLanguageCode, value))
            {
                foreach (var language in _languages)
                {
                    language.IsSelected = string.Equals(language.Code, value, StringComparison.OrdinalIgnoreCase);
                }

                OnPropertyChanged(nameof(SelectedLanguageDisplayName));
                OnPropertyChanged(nameof(SelectedLanguageBadge));
                LocalizationManager.Instance.SetLanguage(value);
                Save();
            }
        }
    }

    public bool IsDarkTheme
    {
        get => _isDarkTheme;
        set
        {
            if (SetProperty(ref _isDarkTheme, value))
            {
                ThemeManager.Apply(value ? "Dark" : "Light");
                Save();
            }
        }
    }

    private void Save()
    {
        try
        {
            _store.Save(new UserPreferences(
                _selectedLanguageCode,
                _isDarkTheme ? "Dark" : "Light"));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            WpfMessageBox.Show(
                WpfApplication.Current?.MainWindow,
                LocalizationManager.Instance.Translate("Ayarlar kaydedilemedi. Bu seçim uygulama yeniden açıldığında korunmayabilir."),
                LocalizationManager.Instance.Translate("Ayarlar"),
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    private void SelectLanguage(object? parameter)
    {
        if (parameter is string languageCode)
        {
            SelectedLanguageCode = languageCode;
        }

        IsLanguagePickerOpen = false;
    }
}

public sealed class LanguageChoiceViewModel(string code, string displayName, bool isSelected) : ObservableObject
{
    private bool _isSelected = isSelected;

    public string Code { get; } = code;

    public string DisplayName { get; } = displayName;

    public bool IsSelected
    {
        get => _isSelected;
        internal set => SetProperty(ref _isSelected, value);
    }
}
