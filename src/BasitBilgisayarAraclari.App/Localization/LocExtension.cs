using System.Windows.Data;
using System.Windows.Markup;

namespace BasitBilgisayarAraclari.App.Localization;

[MarkupExtensionReturnType(typeof(string))]
public sealed class LocExtension : MarkupExtension
{
    public LocExtension()
    {
    }

    public LocExtension(string source)
    {
        Source = source;
    }

    public string Source { get; set; } = string.Empty;

    public override object ProvideValue(IServiceProvider serviceProvider)
    {
        var localizedValue = LocalizationManager.Instance.GetLocalizedValue(Source);
        return new System.Windows.Data.Binding(nameof(LocalizedValue.Value))
        {
            Source = localizedValue,
            Mode = BindingMode.OneWay
        }.ProvideValue(serviceProvider);
    }
}
