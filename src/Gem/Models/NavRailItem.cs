using CommunityToolkit.Mvvm.ComponentModel;
using Wpf.Ui.Controls;

namespace Gem.Models;

public sealed partial class NavRailItem : ObservableObject
{
    public StoreNavSection Section { get; init; }

    [ObservableProperty]
    private string _title = string.Empty;

    public SymbolRegular Icon { get; init; }

    public bool IsBottomPinned { get; init; }

    [ObservableProperty]
    private bool _isSelected;

    [ObservableProperty]
    private int _badgeCount;

    public bool HasBadge => BadgeCount > 0;

    partial void OnBadgeCountChanged(int value)
    {
        OnPropertyChanged(nameof(HasBadge));
    }
}
