using System.Runtime.ExceptionServices;
using System.Reflection;
using System.Threading;
using System.Collections;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using BasitBilgisayarAraclari.App;
using BasitBilgisayarAraclari.App.Modules;
using BasitBilgisayarAraclari.App.Services;
using BasitBilgisayarAraclari.App.ViewModels;
using BasitBilgisayarAraclari.Tests.Fakes;

namespace BasitBilgisayarAraclari.Tests;

public sealed class ShutdownTimerWindowIntegrationTests
{
    [Fact]
    public void WindowHidesRestoresScrollsAtMinimumSizeAndRoutesSafeExitChoices()
    {
        RunOnSta(() =>
        {
            Application? application = null;
            TrayIconService? tray = null;
            object? notifyIcon = null;
            object[]? menuItems = null;
            var choices = new Queue<ShutdownExitChoice>();
            var promptCount = 0;
            MainWindow? window = null;

            try
            {
                application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                var service = new FakeShutdownService();
                var viewModel = new MainWindowViewModel(ToolRegistry.CreateBuiltIns(service));
                tray = new TrayIconService();
                notifyIcon = GetNotifyIcon(tray);
                menuItems = GetTrayMenuItems(notifyIcon);
                window = new MainWindow(
                    viewModel,
                    new ShutdownExitCoordinator(service),
                    tray,
                    () =>
                    {
                        promptCount++;
                        return Task.FromResult(choices.Dequeue());
                    })
                {
                    Width = 880,
                    Height = 640,
                    Left = -2000,
                    Top = -2000
                };

                Assert.True(ReadProperty<bool>(notifyIcon, "Visible"));
                Assert.Equal("Pencereyi göster", ReadProperty<string>(menuItems[0], "Text"));
                Assert.Equal("Zamanlayıcıyı iptal et", ReadProperty<string>(menuItems[1], "Text"));
                Assert.Equal("Uygulamadan çık", ReadProperty<string>(menuItems[3], "Text"));
                Assert.Equal(880, window.MinWidth);
                Assert.Equal(640, window.MinHeight);
                viewModel.OpenToolCommand.Execute(viewModel.Tools.Single(tool => tool.Title == "Zamanlı Kapatma"));

                window.Show();
                window.UpdateLayout();

                var scrollViewer = Assert.IsType<ScrollViewer>(window.FindName("WorkspaceScrollViewer"));
                Assert.Equal(ScrollBarVisibility.Auto, scrollViewer.VerticalScrollBarVisibility);
                Assert.True(scrollViewer.ViewportHeight > 0);
                Assert.True(scrollViewer.ScrollableHeight > 0);

                window.Close();
                Assert.False(window.IsVisible);
                Assert.True(ReadProperty<bool>(notifyIcon, "Visible"));

                PerformClick(menuItems[0]);
                DrainDispatcher();
                Assert.True(window.IsVisible);

                window.WindowState = WindowState.Minimized;
                PerformClick(menuItems[0]);
                DrainDispatcher();
                Assert.Equal(WindowState.Normal, window.WindowState);
                Assert.True(window.IsVisible);

                var timer = Assert.IsType<ShutdownTimerViewModel>(viewModel.ShutdownTimer);
                timer.SecondsInput = "30";
                timer.IsRiskAcknowledged = true;
                timer.ScheduleCommand.ExecuteAsync().GetAwaiter().GetResult();
                Assert.True(ReadProperty<bool>(menuItems[1], "Enabled"));
                Assert.Single(service.ScheduledDelays);

                PerformClick(menuItems[1]);
                DrainDispatcher();
                Assert.False(timer.IsScheduled);
                Assert.False(ReadProperty<bool>(menuItems[1], "Enabled"));
                Assert.Equal(1, service.CancellationCount);

                timer.SecondsInput = "30";
                timer.IsRiskAcknowledged = true;
                timer.ScheduleCommand.ExecuteAsync().GetAwaiter().GetResult();
                Assert.True(ReadProperty<bool>(menuItems[1], "Enabled"));

                choices.Enqueue(ShutdownExitChoice.StayInApplication);
                PerformClick(menuItems[3]);
                DrainDispatcher();
                Assert.True(window.IsVisible);
                Assert.Equal(1, service.CancellationCount);
                Assert.Equal(1, promptCount);

                choices.Enqueue(ShutdownExitChoice.CancelScheduledShutdown);
                PerformClick(menuItems[3]);
                DrainDispatcher();
                Assert.False(window.IsVisible);
                Assert.False(ReadProperty<bool>(notifyIcon, "Visible"));
                Assert.Equal(2, service.CancellationCount);
                Assert.Equal(2, promptCount);
            }
            finally
            {
                try
                {
                    if (window is not null && notifyIcon is not null && menuItems is not null
                        && ReadProperty<bool>(notifyIcon, "Visible"))
                    {
                        choices.Enqueue(ShutdownExitChoice.ContinueScheduledShutdown);
                        PerformClick(menuItems[3]);
                        DrainDispatcher();
                    }
                }
                finally
                {
                    tray?.Dispose();
                    application?.Shutdown();
                }
            }
        });
    }

    private static void RunOnSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception exception)
            {
                failure = exception;
            }
        });

        thread.IsBackground = true;
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "WPF integration test did not finish.");
        if (failure is not null)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }

    private static void DrainDispatcher()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(
            DispatcherPriority.ApplicationIdle,
            new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }

    private static object GetNotifyIcon(TrayIconService tray)
    {
        var notifyIconField = typeof(TrayIconService).GetField("_notifyIcon", BindingFlags.Instance | BindingFlags.NonPublic);
        return Assert.IsAssignableFrom<object>(notifyIconField?.GetValue(tray));
    }

    private static object[] GetTrayMenuItems(object notifyIcon)
    {
        var menu = Assert.IsAssignableFrom<object>(ReadProperty<object>(notifyIcon, "ContextMenuStrip"));
        var itemCollection = Assert.IsAssignableFrom<IEnumerable>(ReadProperty<object>(menu, "Items"));
        var items = itemCollection.Cast<object>().ToArray();
        Assert.Equal(4, items.Length);
        Assert.Equal("ToolStripSeparator", items[2].GetType().Name);
        return items;
    }

    private static T ReadProperty<T>(object instance, string propertyName) =>
        (T)(instance.GetType().GetProperty(propertyName)?.GetValue(instance)
            ?? throw new InvalidOperationException($"Missing property {propertyName}."));

    private static void PerformClick(object menuItem) =>
        menuItem.GetType().GetMethod("PerformClick", Type.EmptyTypes)?.Invoke(menuItem, null);

}
