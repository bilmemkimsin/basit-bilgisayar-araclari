using System.Windows;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Media;
using WpfApplication = System.Windows.Application;

namespace BasitBilgisayarAraclari.App.Services;

public static class ThemeManager
{
    // Per-window caption colors and rounded-corner preference are supported by Windows 11 22000+.
    private const int DwmwaUseImmersiveDarkMode = 20;
    private const int DwmwaWindowCornerPreference = 33;
    private const int DwmwaBorderColor = 34;
    private const int DwmwaCaptionColor = 35;
    private const int DwmwaTextColor = 36;
    private const int DwmWindowCornerRound = 2;

    public static string CurrentTheme { get; private set; } = "Dark";

    public static event EventHandler? ThemeChanged;

    public static void Apply(string theme)
    {
        var normalizedTheme = string.Equals(theme, "Light", StringComparison.OrdinalIgnoreCase)
            ? "Light"
            : "Dark";
        if (WpfApplication.Current is not null)
        {
            var dictionaries = WpfApplication.Current.Resources.MergedDictionaries;
            var existingIndex = -1;
            for (var index = 0; index < dictionaries.Count; index++)
            {
                var source = dictionaries[index].Source?.OriginalString.Replace('\\', '/');
                if (source?.EndsWith("Themes/Light.xaml", StringComparison.OrdinalIgnoreCase) == true ||
                    source?.EndsWith("Themes/Dark.xaml", StringComparison.OrdinalIgnoreCase) == true)
                {
                    existingIndex = index;
                    break;
                }
            }

            var dictionary = new ResourceDictionary
            {
                Source = new Uri($"pack://application:,,,/Themes/{normalizedTheme}.xaml", UriKind.Absolute)
            };
            if (existingIndex >= 0)
            {
                dictionaries[existingIndex] = dictionary;
            }
            else
            {
                dictionaries.Insert(0, dictionary);
            }
        }

        if (!string.Equals(CurrentTheme, normalizedTheme, StringComparison.Ordinal))
        {
            CurrentTheme = normalizedTheme;
            ThemeChanged?.Invoke(null, EventArgs.Empty);
        }

        if (WpfApplication.Current is { } application)
        {
            foreach (Window window in application.Windows)
            {
                ApplyWindowChrome(window);
            }
        }
    }

    public static void RegisterWindow(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);
        window.SourceInitialized += (_, _) => ApplyWindowChrome(window);
        ApplyWindowChrome(window);
    }

    private static void ApplyWindowChrome(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero)
        {
            return;
        }

        try
        {
            var darkMode = string.Equals(CurrentTheme, "Dark", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
            SetDwmAttribute(handle, DwmwaUseImmersiveDarkMode, darkMode);
            SetDwmAttribute(handle, DwmwaWindowCornerPreference, DwmWindowCornerRound);
            SetDwmAttribute(handle, DwmwaCaptionColor, GetColorRef("WindowBackgroundBrush"));
            SetDwmAttribute(handle, DwmwaBorderColor, GetColorRef("BorderBrush"));
            SetDwmAttribute(handle, DwmwaTextColor, GetColorRef("TextPrimaryBrush"));
        }
        catch (DllNotFoundException)
        {
            // DWM window-frame styling is optional on systems without dwmapi.dll.
        }
        catch (EntryPointNotFoundException)
        {
            // Older Windows versions keep their native caption and corner defaults.
        }
    }

    private static int GetColorRef(string resourceKey)
    {
        if (WpfApplication.Current?.TryFindResource(resourceKey) is SolidColorBrush brush)
        {
            var color = brush.Color;
            return color.R | (color.G << 8) | (color.B << 16);
        }

        return unchecked((int)0xFFFFFFFF);
    }

    private static void SetDwmAttribute(IntPtr handle, int attribute, int value)
    {
        _ = DwmSetWindowAttribute(handle, attribute, ref value, sizeof(int));
    }

    [DllImport("dwmapi.dll", ExactSpelling = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int valueSize);
}
