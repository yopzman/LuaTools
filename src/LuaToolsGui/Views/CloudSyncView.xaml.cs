using System.Windows;
using System.Windows.Controls;
using LuaToolsGui.ViewModels;

namespace LuaToolsGui.Views;

public partial class CloudSyncView : UserControl
{
    private readonly CloudSyncViewModel _viewModel;

    public CloudSyncView(CloudSyncViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;

        Loaded += async (_, _) =>
        {
            try
            {
                await _viewModel.InitializeAsync();
            }
            catch { }
        };
    }

    private void Tab_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string tag } && int.TryParse(tag, out int index))
        {
            _viewModel.SelectedTabIndex = index;
        }
    }
}
