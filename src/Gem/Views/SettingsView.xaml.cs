using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace Gem.Views;

public partial class SettingsView : UserControl
{
    private static readonly ConditionalWeakTable<UIElement, object> HookedPopupRoots = new();

    public SettingsView()
    {
        InitializeComponent();
    }

    internal bool IsAnyDropDownOpen =>
        ThemeComboBox.IsDropDownOpen || LanguageComboBox.IsDropDownOpen;

    private void ComboBox_OnDropDownOpened(object? sender, EventArgs e)
    {
        if (sender is not ComboBox comboBox)
            return;

        Popup? popup = comboBox.Template?.FindName("PART_Popup", comboBox) as Popup
            ?? FindVisualChild<Popup>(comboBox);
        if (popup?.Child is null)
            return;

        HookPopupRoot(popup.Child);
    }

    private void ComboBox_OnMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (sender is ComboBox comboBox && comboBox.IsDropDownOpen)
            e.Handled = true;
    }

    private void SettingsContentPanel_OnMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (IsAnyDropDownOpen)
            e.Handled = true;
    }

    private static void HookPopupRoot(UIElement popupRoot)
    {
        if (HookedPopupRoots.TryGetValue(popupRoot, out _))
            return;

        HookedPopupRoots.Add(popupRoot, new object());
        popupRoot.MouseWheel += static (_, args) => args.Handled = true;
    }

    private static T? FindVisualChild<T>(DependencyObject root) where T : DependencyObject
    {
        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, i);
            if (child is T matched)
                return matched;

            T? nested = FindVisualChild<T>(child);
            if (nested is not null)
                return nested;
        }

        return null;
    }
}
