using System.Drawing;
using System.IO;
using BasitBilgisayarAraclari.App.Localization;
using Forms = System.Windows.Forms;

namespace BasitBilgisayarAraclari.App.Services;

public sealed class TrayIconService : ITrayIconService
{
    private readonly Forms.NotifyIcon _notifyIcon;
    private readonly Forms.ToolStripMenuItem _cancelTimerMenuItem;
    private readonly Forms.ToolStripMenuItem _showWindowMenuItem;
    private readonly Forms.ToolStripMenuItem _exitMenuItem;
    private readonly Forms.ContextMenuStrip _contextMenu;
    private readonly Icon _applicationIcon;
    private readonly Stream _iconResourceStream;

    public TrayIconService()
    {
        var iconResource = System.Windows.Application.GetResourceStream(
            new Uri("pack://application:,,,/Assets/CryingBabyMascot.ico", UriKind.Absolute))
            ?? throw new InvalidOperationException("Uygulama tepsi simgesi yüklenemedi.");
        _iconResourceStream = iconResource.Stream;
        _applicationIcon = new Icon(_iconResourceStream);

        var menu = new Forms.ContextMenuStrip();
        _contextMenu = menu;
        _showWindowMenuItem = new Forms.ToolStripMenuItem();
        _showWindowMenuItem.Click += (_, _) => ShowWindowRequested?.Invoke(this, EventArgs.Empty);
        menu.Items.Add(_showWindowMenuItem);
        _cancelTimerMenuItem = new Forms.ToolStripMenuItem("Zamanlayıcıyı iptal et")
        {
            Enabled = false
        };
        _cancelTimerMenuItem.Click += (_, _) => CancelTimerRequested?.Invoke(this, EventArgs.Empty);
        menu.Items.Add(_cancelTimerMenuItem);
        menu.Items.Add(new Forms.ToolStripSeparator());
        _exitMenuItem = new Forms.ToolStripMenuItem();
        _exitMenuItem.Click += (_, _) => ExitRequested?.Invoke(this, EventArgs.Empty);
        menu.Items.Add(_exitMenuItem);

        _notifyIcon = new Forms.NotifyIcon
        {
            Icon = _applicationIcon,
            Text = string.Empty,
            ContextMenuStrip = menu,
            Visible = true
        };
        _notifyIcon.DoubleClick += (_, _) => ShowWindowRequested?.Invoke(this, EventArgs.Empty);
        LocalizationManager.Instance.LanguageChanged += OnLanguageChanged;
        ThemeManager.ThemeChanged += OnThemeChanged;
        UpdateLocalizedText();
        UpdateTheme();
    }

    public event EventHandler? ShowWindowRequested;

    public event EventHandler? CancelTimerRequested;

    public event EventHandler? ExitRequested;

    public void SetTimerScheduled(bool isScheduled) => _cancelTimerMenuItem.Enabled = isScheduled;

    private void OnLanguageChanged(object? sender, EventArgs e) => UpdateLocalizedText();

    private void OnThemeChanged(object? sender, EventArgs e) => UpdateTheme();

    private void UpdateLocalizedText()
    {
        var localization = LocalizationManager.Instance;
        _showWindowMenuItem.Text = localization.Translate("Pencereyi göster");
        _cancelTimerMenuItem.Text = localization.Translate("Zamanlayıcıyı iptal et");
        _exitMenuItem.Text = localization.Translate("Uygulamadan çık");
        _notifyIcon.Text = localization.Translate("Basit Bilgisayar Araçları");
    }

    private void UpdateTheme()
    {
        var dark = string.Equals(ThemeManager.CurrentTheme, "Dark", StringComparison.Ordinal);
        var background = dark ? Color.FromArgb(32, 37, 49) : Color.FromArgb(255, 255, 255);
        var foreground = dark ? Color.FromArgb(240, 242, 248) : Color.FromArgb(32, 40, 59);
        _contextMenu.BackColor = background;
        _contextMenu.ForeColor = foreground;
        foreach (Forms.ToolStripItem item in _contextMenu.Items)
        {
            item.BackColor = background;
            item.ForeColor = foreground;
        }
    }

    public void Dispose()
    {
        LocalizationManager.Instance.LanguageChanged -= OnLanguageChanged;
        ThemeManager.ThemeChanged -= OnThemeChanged;
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        _applicationIcon.Dispose();
        _iconResourceStream.Dispose();
    }
}
