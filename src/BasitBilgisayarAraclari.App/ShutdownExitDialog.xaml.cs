using BasitBilgisayarAraclari.App.Services;
using WpfRoutedEventArgs = System.Windows.RoutedEventArgs;
using WpfWindow = System.Windows.Window;

namespace BasitBilgisayarAraclari.App;

public partial class ShutdownExitDialog : WpfWindow
{
    public ShutdownExitDialog()
    {
        InitializeComponent();
        ThemeManager.RegisterWindow(this);
    }

    public ShutdownExitChoice Choice { get; private set; } = ShutdownExitChoice.StayInApplication;

    private void Continue_Click(object sender, WpfRoutedEventArgs e)
    {
        Choice = ShutdownExitChoice.ContinueScheduledShutdown;
        DialogResult = true;
    }

    private void CancelAndExit_Click(object sender, WpfRoutedEventArgs e)
    {
        Choice = ShutdownExitChoice.CancelScheduledShutdown;
        DialogResult = true;
    }

    private void StayOpen_Click(object sender, WpfRoutedEventArgs e)
    {
        Choice = ShutdownExitChoice.StayInApplication;
        DialogResult = false;
    }
}
