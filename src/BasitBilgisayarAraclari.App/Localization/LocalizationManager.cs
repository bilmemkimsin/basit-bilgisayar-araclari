using System.Collections.Concurrent;
using System.ComponentModel;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace BasitBilgisayarAraclari.App.Localization;

public sealed record LanguageOption(string Code, string DisplayName);

public sealed class LocalizationManager
{
    private static readonly Regex PlaceholderRegex = new(@"\{(?<token>[^{}]+)\}", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly IReadOnlyList<LanguageOption> SupportedLanguages = Array.AsReadOnly(new[]
    {
        new LanguageOption("tr", "Türkçe"),
        new LanguageOption("en", "English"),
        new LanguageOption("de", "Deutsch"),
        new LanguageOption("fr", "Français"),
        new LanguageOption("zh-Hans", "简体中文"),
        new LanguageOption("ko", "한국어"),
        new LanguageOption("ja", "日本語"),
        new LanguageOption("es", "Español")
    });

    private readonly ConcurrentDictionary<string, IReadOnlyDictionary<string, string>> _catalogues = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, LocalizedValue> _localizedValues = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Regex> _templateRegexes = new(StringComparer.Ordinal);
    private string _languageCode = "tr";

    private LocalizationManager()
    {
    }

    public static LocalizationManager Instance { get; } = new();

    public static IReadOnlyList<LanguageOption> Languages => SupportedLanguages;

    public string LanguageCode => _languageCode;

    public CultureInfo Culture { get; private set; } = CultureInfo.GetCultureInfo("tr-TR");

    public event EventHandler? LanguageChanged;

    public void SetLanguage(string? languageCode)
    {
        var normalizedCode = SupportedLanguages.FirstOrDefault(language =>
            string.Equals(language.Code, languageCode, StringComparison.OrdinalIgnoreCase))?.Code ?? "tr";
        if (string.Equals(_languageCode, normalizedCode, StringComparison.OrdinalIgnoreCase))
        {
            ApplyCulture(normalizedCode);
            return;
        }

        _languageCode = normalizedCode;
        ApplyCulture(normalizedCode);
        _ = GetCatalogue(normalizedCode);
        LanguageChanged?.Invoke(this, EventArgs.Empty);
    }

    public string Translate(string source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (_languageCode == "tr")
        {
            return source;
        }

        return GetCatalogue(_languageCode).TryGetValue(source, out var translation)
            ? translation
            : source;
    }

    public string TranslateUserMessage(string source, CultureInfo? sourceCulture = null)
        => TranslateUserMessageCore(source, sourceCulture ?? CultureInfo.GetCultureInfo("tr-TR"), depth: 0);

    private string TranslateUserMessageCore(string source, CultureInfo sourceCulture, int depth)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (depth >= 4)
        {
            return source;
        }

        var catalogue = GetCatalogue(_languageCode);
        if (catalogue.TryGetValue(source, out var exact))
        {
            return exact;
        }

        foreach (var (template, translation) in catalogue)
        {
            if (!template.Contains('{') || !translation.Contains('{'))
            {
                continue;
            }

            var match = _templateRegexes.GetOrAdd(template, CreateTemplateRegex).Match(source);
            if (!match.Success)
            {
                continue;
            }

            var arguments = new Dictionary<string, object?>(StringComparer.Ordinal);
            var sourceTokens = PlaceholderRegex.Matches(template)
                .Select(item => item.Groups["token"].Value)
                .ToArray();
            var translatedTokens = PlaceholderRegex.Matches(translation)
                .Select(item => item.Groups["token"].Value)
                .ToArray();
            if (sourceTokens.Length != translatedTokens.Length)
            {
                continue;
            }

            for (var index = 0; index < sourceTokens.Length; index++)
            {
                var token = sourceTokens[index];
                var name = token.Split(':', 2)[0];
                var captured = match.Groups[$"p{index}"].Value;
                var format = token.Contains(':') ? token[(token.IndexOf(':') + 1)..] : null;
                var value = ParseCapturedValue(captured, format, sourceCulture);
                if (value is string nestedText &&
                    (name.EndsWith(".Message", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(name, "backupText", StringComparison.Ordinal) ||
                     token.StartsWith("string.Join", StringComparison.Ordinal)))
                {
                    value = token.StartsWith("string.Join", StringComparison.Ordinal)
                        ? string.Join("; ", nestedText.Split("; ", StringSplitOptions.None)
                            .Select(part => TranslateUserMessageCore(part, sourceCulture, depth + 1)))
                        : TranslateUserMessageCore(nestedText, sourceCulture, depth + 1);
                }

                arguments[name] = value;
            }

            return FormatNamed(translation, arguments);
        }

        return source;
    }

    public string Format(string sourceTemplate, IReadOnlyDictionary<string, object?> arguments)
    {
        ArgumentNullException.ThrowIfNull(sourceTemplate);
        ArgumentNullException.ThrowIfNull(arguments);
        var template = Translate(sourceTemplate);
        return FormatNamed(template, arguments);
    }

    public LocalizedValue GetLocalizedValue(string source) =>
        _localizedValues.GetOrAdd(source, key => new LocalizedValue(this, key));

    private IReadOnlyDictionary<string, string> GetCatalogue(string languageCode) =>
        _catalogues.GetOrAdd(languageCode, LoadCatalogue);

    private static IReadOnlyDictionary<string, string> LoadCatalogue(string languageCode)
    {
        if (languageCode == "tr")
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }

        var uri = new Uri($"pack://application:,,,/localization/strings.{languageCode.ToLowerInvariant()}.json", UriKind.Absolute);
        var resource = System.Windows.Application.GetResourceStream(uri);
        if (resource is null)
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }

        using var stream = resource.Stream;

        return JsonSerializer.Deserialize<Dictionary<string, string>>(stream) ??
            new Dictionary<string, string>(StringComparer.Ordinal);
    }

    private void ApplyCulture(string languageCode)
    {
        var cultureName = languageCode switch
        {
            "tr" => "tr-TR",
            "en" => "en-US",
            "de" => "de-DE",
            "fr" => "fr-FR",
            "zh-Hans" => "zh-Hans",
            "ko" => "ko-KR",
            "ja" => "ja-JP",
            "es" => "es-ES",
            _ => "tr-TR"
        };
        Culture = CultureInfo.GetCultureInfo(cultureName);
        CultureInfo.CurrentCulture = Culture;
        CultureInfo.CurrentUICulture = Culture;
        CultureInfo.DefaultThreadCurrentCulture = Culture;
        CultureInfo.DefaultThreadCurrentUICulture = Culture;
    }

    private string FormatNamed(string template, IReadOnlyDictionary<string, object?> arguments) =>
        PlaceholderRegex.Replace(template, match =>
        {
            var token = match.Groups["token"].Value;
            var separator = token.IndexOf(':');
            var name = separator < 0 ? token : token[..separator];
            var format = separator < 0 ? null : token[(separator + 1)..];
            if (!arguments.TryGetValue(name, out var value) || value is null)
            {
                return match.Value;
            }

            return value is IFormattable formattable
                ? formattable.ToString(format, Culture)
                : value.ToString() ?? string.Empty;
        });

    private static object ParseCapturedValue(string captured, string? format, CultureInfo sourceCulture)
    {
        if (string.IsNullOrWhiteSpace(format))
        {
            return captured;
        }

        if (format.StartsWith("N", StringComparison.OrdinalIgnoreCase) &&
            long.TryParse(captured, NumberStyles.Number, sourceCulture, out var integer))
        {
            return integer;
        }

        if (format.StartsWith("g", StringComparison.OrdinalIgnoreCase) &&
            DateTime.TryParse(captured, sourceCulture, DateTimeStyles.None, out var date))
        {
            return date;
        }

        return captured;
    }

    private static Regex CreateTemplateRegex(string template)
    {
        var pattern = new StringBuilder("^");
        var position = 0;
        var index = 0;
        foreach (Match placeholder in PlaceholderRegex.Matches(template))
        {
            pattern.Append(Regex.Escape(template[position..placeholder.Index]));
            pattern.Append($"(?<p{index++}>.*?)");
            position = placeholder.Index + placeholder.Length;
        }

        pattern.Append(Regex.Escape(template[position..]));
        pattern.Append('$');
        return new Regex(pattern.ToString(), RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.Singleline);
    }
}

public sealed class LocalizedValue : INotifyPropertyChanged
{
    private readonly LocalizationManager _localization;

    internal LocalizedValue(LocalizationManager localization, string source)
    {
        _localization = localization;
        Source = source;
        _localization.LanguageChanged += OnLanguageChanged;
    }

    public string Source { get; }

    public string Value => _localization.Translate(Source);

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnLanguageChanged(object? sender, EventArgs e) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Value)));
}
