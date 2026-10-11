using BasitBilgisayarAraclari.App.Core;
using BasitBilgisayarAraclari.App.Localization;
using BasitBilgisayarAraclari.App.Modules;

namespace BasitBilgisayarAraclari.App.ViewModels;

public sealed class ToolCardViewModel : ObservableObject, IDisposable
{
    private bool _disposed;

    public ToolCardViewModel(IToolModule module)
    {
        Module = module ?? throw new ArgumentNullException(nameof(module));
        LocalizationManager.Instance.LanguageChanged += OnLanguageChanged;
    }

    public IToolModule Module { get; }

    public string Title => LocalizationManager.Instance.Translate(Module.Descriptor.Title);

    public string Description => LocalizationManager.Instance.Translate(Module.Descriptor.Description);

    public string Category => LocalizationManager.Instance.Translate(Module.Descriptor.Category);

    public string IconGlyph => Module.Descriptor.IconGlyph;

    public string IconBackground => Module.Descriptor.IconBackground;

    public string IconForeground => Module.Descriptor.IconForeground;

    public string ToolLabel => LocalizationManager.Instance.Translate(Module.Descriptor.ToolLabel);

    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(Description));
        OnPropertyChanged(nameof(Category));
        OnPropertyChanged(nameof(ToolLabel));
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        LocalizationManager.Instance.LanguageChanged -= OnLanguageChanged;
    }
}
