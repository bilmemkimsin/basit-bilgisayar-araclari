using System.Windows;

namespace BasitBilgisayarAraclari.App;

public partial class SplashWindow : Window
{
    public SplashWindow()
    {
        InitializeComponent();
    }

    public async Task ShowForStartupAsync()
    {
        Show();
        await Task.Delay(TimeSpan.FromMilliseconds(1850));
        if (IsVisible)
        {
            Close();
        }
    }
}
