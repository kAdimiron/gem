using System.Windows;
using System.Windows.Controls;
using Gem.Models;
using Gem.ViewModels;

namespace Gem.Views;

public partial class HomeView : UserControl
{
    public HomeView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.OldValue is MainViewModel oldVm)
        {
            oldVm.SpeedHistoryUpdated -= OnSpeedHistoryUpdated;
            oldVm.TrafficHistoryUpdated -= OnTrafficHistoryUpdated;
        }

        if (e.NewValue is MainViewModel newVm)
        {
            newVm.SpeedHistoryUpdated += OnSpeedHistoryUpdated;
            newVm.TrafficHistoryUpdated += OnTrafficHistoryUpdated;
        }
    }

    private void OnSpeedHistoryUpdated(IReadOnlyList<long> down, IReadOnlyList<long> up)
    {
        SpeedChart.UpdateSeries(down, up);
    }

    private void OnTrafficHistoryUpdated(IReadOnlyList<DailyTrafficPoint> days)
    {
        TrafficChart.UpdateSeries(days);
    }
}
