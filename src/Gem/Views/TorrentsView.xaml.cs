using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Gem.Models;
using Gem.ViewModels;

namespace Gem.Views;

public partial class TorrentsView : UserControl
{
    private const double ClickMaxDriftPx = 8.0;

    private MainViewModel? _subscribedVm;
    private bool _isSyncingSelection;
    private ListViewItem? _pressedCardItem;
    private Point _cardPressPoint;

    public TorrentsView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_subscribedVm is not null)
        {
            _subscribedVm.SelectedTorrents.CollectionChanged -= OnViewModelSelectedTorrentsChanged;
            _subscribedVm = null;
        }

        if (DataContext is MainViewModel vm)
        {
            _subscribedVm = vm;
            _subscribedVm.SelectedTorrents.CollectionChanged += OnViewModelSelectedTorrentsChanged;
        }
    }

    private void OnViewModelSelectedTorrentsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (_isSyncingSelection || _subscribedVm is null)
            return;

        if (_subscribedVm.SelectedTorrents.Count == 0 && TorrentsListView.SelectedItems.Count > 0)
        {
            _isSyncingSelection = true;
            try
            {
                TorrentsListView.SelectedItems.Clear();
            }
            finally
            {
                _isSyncingSelection = false;
            }
        }
    }

    private void TorrentsListView_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isSyncingSelection || DataContext is not MainViewModel vm)
            return;

        _isSyncingSelection = true;
        try
        {
            vm.UpdateSelectedTorrents(TorrentsListView.SelectedItems.OfType<TorrentItemModel>());
        }
        finally
        {
            _isSyncingSelection = false;
        }
    }

    private void TorrentsListView_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && TorrentsListView.SelectedItems.Count > 0)
        {
            TorrentsListView.SelectedItems.Clear();
            e.Handled = true;
        }
    }

    private void ListViewItem_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is ListViewItem item)
        {
            if (!item.IsSelected)
            {
                TorrentsListView.SelectedItems.Clear();
                item.IsSelected = true;
            }

            item.Focus();
        }
    }

    private void ListViewItem_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (sender is not ListViewItem item)
        {
            e.Handled = true;
            return;
        }

        if (!item.IsSelected)
        {
            TorrentsListView.SelectedItems.Clear();
            item.IsSelected = true;
        }
    }

    private void TorrentsListView_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        var origin = e.OriginalSource as DependencyObject;
        if (IsDescendantOf<ScrollBar>(origin, TorrentsListView) ||
            IsDescendantOf<ButtonBase>(origin, TorrentsListView))
        {
            _pressedCardItem = null;
            return;
        }

        var hitCard = FindAncestor<ListViewItem>(origin, TorrentsListView);
        if (hitCard is null)
        {
            _pressedCardItem = null;
            if (TorrentsListView.SelectedItems.Count > 0)
                TorrentsListView.SelectedItems.Clear();
            return;
        }

        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            _pressedCardItem = null;
            hitCard.IsSelected = !hitCard.IsSelected;
            hitCard.Focus();
            e.Handled = true;
            return;
        }

        // Plain left-click opens Details on release without selecting the card.
        _pressedCardItem = hitCard;
        _cardPressPoint = e.GetPosition(TorrentsListView);
        e.Handled = true;
    }

    private void TorrentsListView_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        var pressed = _pressedCardItem;
        _pressedCardItem = null;
        if (pressed is null)
            return;

        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
            return;

        var origin = e.OriginalSource as DependencyObject;
        if (IsDescendantOf<ButtonBase>(origin, pressed))
            return;

        Point release = e.GetPosition(TorrentsListView);
        if (Math.Abs(release.X - _cardPressPoint.X) > ClickMaxDriftPx ||
            Math.Abs(release.Y - _cardPressPoint.Y) > ClickMaxDriftPx)
        {
            return;
        }

        if (DataContext is MainViewModel vm && pressed.DataContext is TorrentItemModel torrent)
        {
            if (TorrentsListView.SelectedItems.Count > 0)
                TorrentsListView.SelectedItems.Clear();

            vm.OpenTorrentDetailsCommand.Execute(torrent);
            e.Handled = true;
        }
    }

    private static T? FindAncestor<T>(DependencyObject? element, DependencyObject stopAt) where T : DependencyObject
    {
        var current = element;
        while (current is not null && !ReferenceEquals(current, stopAt))
        {
            if (current is T match)
                return match;

            current = current is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(current)
                : LogicalTreeHelper.GetParent(current);
        }

        return null;
    }

    private static bool IsDescendantOf<T>(DependencyObject? element, DependencyObject stopAt) where T : DependencyObject =>
        FindAncestor<T>(element, stopAt) is not null;
}
