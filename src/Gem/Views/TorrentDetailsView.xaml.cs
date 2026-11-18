using System.Windows;
using System.Windows.Controls;
using Gem.Models;
using Gem.ViewModels;

namespace Gem.Views;

public partial class TorrentDetailsView : UserControl
{
    public TorrentDetailsView()
    {
        InitializeComponent();
    }

    private async void FileCheckBox_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox cb ||
            cb.DataContext is not TorrentFileModel file ||
            DataContext is not MainViewModel vm)
        {
            return;
        }

        await vm.ToggleFileSelectedAsync(file, cb.IsChecked == true);
    }
}
