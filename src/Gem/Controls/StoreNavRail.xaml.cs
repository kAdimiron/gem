using System.Collections;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Gem.Models;

namespace Gem.Controls;

public partial class StoreNavRail : UserControl
{
    public static readonly DependencyProperty TopItemsProperty = DependencyProperty.Register(
        nameof(TopItems),
        typeof(IEnumerable),
        typeof(StoreNavRail),
        new PropertyMetadata(null, OnNavItemsPropertyChanged));

    public static readonly DependencyProperty BottomItemsProperty = DependencyProperty.Register(
        nameof(BottomItems),
        typeof(IEnumerable),
        typeof(StoreNavRail),
        new PropertyMetadata(null, OnNavItemsPropertyChanged));

    public static readonly DependencyProperty SelectCommandProperty = DependencyProperty.Register(
        nameof(SelectCommand),
        typeof(ICommand),
        typeof(StoreNavRail));

    private readonly List<NavRailItem> _trackedItems = [];
    private INotifyCollectionChanged? _trackedTopCollection;
    private INotifyCollectionChanged? _trackedBottomCollection;
    private bool _hasInitializedIndicator;
    private double _currentIndicatorY;

    public IEnumerable? TopItems
    {
        get => (IEnumerable?)GetValue(TopItemsProperty);
        set => SetValue(TopItemsProperty, value);
    }

    public IEnumerable? BottomItems
    {
        get => (IEnumerable?)GetValue(BottomItemsProperty);
        set => SetValue(BottomItemsProperty, value);
    }

    public ICommand? SelectCommand
    {
        get => (ICommand?)GetValue(SelectCommandProperty);
        set => SetValue(SelectCommandProperty, value);
    }

    public StoreNavRail()
    {
        InitializeComponent();
        Loaded += (_, _) => ScheduleIndicatorUpdate(animate: false);
        RootRailGrid.SizeChanged += (_, _) => UpdateIndicatorPosition(animate: false);
    }

    private static void OnNavItemsPropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is StoreNavRail rail)
            rail.ReattachItemSubscriptions();
    }

    private void ReattachItemSubscriptions()
    {
        foreach (var item in _trackedItems)
            item.PropertyChanged -= OnNavItemPropertyChanged;
        _trackedItems.Clear();

        if (_trackedTopCollection is not null)
        {
            _trackedTopCollection.CollectionChanged -= OnItemsCollectionChanged;
            _trackedTopCollection = null;
        }

        if (_trackedBottomCollection is not null)
        {
            _trackedBottomCollection.CollectionChanged -= OnItemsCollectionChanged;
            _trackedBottomCollection = null;
        }

        if (TopItems is INotifyCollectionChanged topObservable)
        {
            _trackedTopCollection = topObservable;
            _trackedTopCollection.CollectionChanged += OnItemsCollectionChanged;
        }

        if (BottomItems is INotifyCollectionChanged bottomObservable)
        {
            _trackedBottomCollection = bottomObservable;
            _trackedBottomCollection.CollectionChanged += OnItemsCollectionChanged;
        }

        SubscribeCollection(TopItems);
        SubscribeCollection(BottomItems);

        ScheduleIndicatorUpdate(animate: false);
    }

    private void SubscribeCollection(IEnumerable? items)
    {
        if (items is null)
            return;

        foreach (object? entry in items)
        {
            if (entry is NavRailItem item)
            {
                item.PropertyChanged += OnNavItemPropertyChanged;
                _trackedItems.Add(item);
            }
        }
    }

    private void OnItemsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        ReattachItemSubscriptions();
    }

    private void OnNavItemPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(NavRailItem.IsSelected) && sender is NavRailItem { IsSelected: true })
        {
            ScheduleIndicatorUpdate(animate: _hasInitializedIndicator);
        }
    }

    private void ScheduleIndicatorUpdate(bool animate)
    {
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => UpdateIndicatorPosition(animate));
    }

    private void UpdateIndicatorPosition(bool animate)
    {
        if (!IsLoaded || RootRailGrid.ActualHeight <= 0)
            return;

        NavRailItem? selected = _trackedItems.FirstOrDefault(i => i.IsSelected);
        if (selected is null)
        {
            SlidingIndicator.Opacity = 0;
            return;
        }

        FrameworkElement? container =
            TopItemsControl.ItemContainerGenerator.ContainerFromItem(selected) as FrameworkElement
            ?? BottomItemsControl.ItemContainerGenerator.ContainerFromItem(selected) as FrameworkElement;

        if (container is null || container.ActualHeight <= 0 || !IsAncestorOf(container))
            return;

        double targetY = container
            .TranslatePoint(new Point(0, (container.ActualHeight - SlidingIndicator.Height) / 2.0), RootRailGrid)
            .Y;

        if (!double.IsFinite(targetY))
            return;

        SlidingIndicator.Opacity = 1;

        if (!animate || !_hasInitializedIndicator)
        {
            IndicatorTranslate.BeginAnimation(TranslateTransform.YProperty, null);
            IndicatorScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            IndicatorTranslate.Y = targetY;
            IndicatorScale.ScaleY = 1;
            _currentIndicatorY = targetY;
            _hasInitializedIndicator = true;
            return;
        }

        if (Math.Abs(targetY - _currentIndicatorY) < 0.5)
            return;

        var slideAnim = new DoubleAnimation
        {
            From = _currentIndicatorY,
            To = targetY,
            Duration = TimeSpan.FromMilliseconds(260),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        IndicatorTranslate.BeginAnimation(TranslateTransform.YProperty, slideAnim);

        var stretchAnim = new DoubleAnimationUsingKeyFrames();
        stretchAnim.KeyFrames.Add(new EasingDoubleKeyFrame(1.0, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        stretchAnim.KeyFrames.Add(new EasingDoubleKeyFrame(1.38, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(110)), new CubicEase { EasingMode = EasingMode.EaseOut }));
        stretchAnim.KeyFrames.Add(new EasingDoubleKeyFrame(1.0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(260)), new CubicEase { EasingMode = EasingMode.EaseOut }));
        IndicatorScale.BeginAnimation(ScaleTransform.ScaleYProperty, stretchAnim);

        _currentIndicatorY = targetY;
    }
}
