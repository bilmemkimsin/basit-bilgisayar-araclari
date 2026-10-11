using BasitBilgisayarAraclari.App.Modules;
using BasitBilgisayarAraclari.App.ViewModels;

namespace BasitBilgisayarAraclari.Tests;

public sealed class DashboardNavigationTests
{
    [Fact]
    public void OpeningAndClosingAToolWorkspaceUpdatesDashboardState()
    {
        var viewModel = new MainWindowViewModel(ToolRegistry.CreateBuiltIns());
        var shutdownTool = Assert.Single(viewModel.Tools, tool => tool.Title == "Zamanlı Kapatma");

        Assert.False(viewModel.IsWorkspaceVisible);
        Assert.True(viewModel.OpenToolCommand.CanExecute(shutdownTool));

        viewModel.OpenToolCommand.Execute(shutdownTool);

        Assert.True(viewModel.IsWorkspaceVisible);
        Assert.Equal("Zamanlı Kapatma", viewModel.ActiveWorkspace?.Title);

        viewModel.ShowDashboardCommand.Execute(null);

        Assert.False(viewModel.IsWorkspaceVisible);
        Assert.Null(viewModel.ActiveWorkspace);
    }
}
