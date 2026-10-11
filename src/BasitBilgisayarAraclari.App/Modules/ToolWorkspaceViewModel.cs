using BasitBilgisayarAraclari.App.Core;
using BasitBilgisayarAraclari.App.Localization;

namespace BasitBilgisayarAraclari.App.Modules;

public class ToolWorkspaceViewModel : ObservableObject, IDisposable
{
    private readonly string _title;
    private readonly string _summary;
    private readonly string _nextStep;
    private readonly string _toolLabel;
    private bool _disposed;

    public ToolWorkspaceViewModel(
        string title,
        string summary,
        string nextStep,
        string iconGlyph,
        string iconBackground,
        string iconForeground,
        string toolLabel)
    {
        _title = title;
        _summary = summary;
        _nextStep = nextStep;
        _toolLabel = toolLabel;
        IconGlyph = iconGlyph;
        IconBackground = iconBackground;
        IconForeground = iconForeground;
        LocalizationManager.Instance.LanguageChanged += OnLanguageChanged;
    }

    public string Title => LocalizationManager.Instance.Translate(_title);

    public string Summary => LocalizationManager.Instance.Translate(_summary);

    public string NextStep => LocalizationManager.Instance.Translate(_nextStep);

    public string IconGlyph { get; }

    public string IconBackground { get; }

    public string IconForeground { get; }

    public string ToolLabel => LocalizationManager.Instance.Translate(_toolLabel);

    protected virtual void OnLanguageChanged(object? sender, EventArgs e)
    {
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(Summary));
        OnPropertyChanged(nameof(NextStep));
        OnPropertyChanged(nameof(ToolLabel));
    }

    public virtual void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        LocalizationManager.Instance.LanguageChanged -= OnLanguageChanged;
    }
}
