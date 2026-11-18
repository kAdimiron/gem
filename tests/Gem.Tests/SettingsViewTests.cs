using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using Gem.Services;
using Gem.Views;

namespace Gem.Tests;

[Collection("Localization")]
public sealed class SettingsViewTests
{
    [Fact]
    public void OpenDropDown_PreventsMouseWheelFromScrollingOuterSettingsScrollViewer()
    {
        RunOnStaThread(() =>
        {
            var view = new SettingsView();
            var window = new Window
            {
                Content = view,
                Width = 800,
                Height = 400,
                ShowActivated = false,
                ShowInTaskbar = false,
                WindowStyle = WindowStyle.None,
                AllowsTransparency = true,
                Opacity = 0
            };

            try
            {
                window.Show();
                window.UpdateLayout();

                var scrollViewer = (ScrollViewer)view.FindName("SettingsScrollViewer")!;
                var comboBox = (ComboBox)view.FindName("LanguageComboBox")!;
                var contentPanel = (Panel)view.FindName("SettingsContentPanel")!;
                comboBox.ItemsSource = LocalizationService.Instance.SupportedLanguages;

                Assert.True(scrollViewer.ScrollableHeight > 0);
                Assert.Equal(0, scrollViewer.VerticalOffset);

                // When closed, wheel on content scrolls SettingsScrollViewer normally.
                var closedPanelArgs = CreateWheelArgs();
                contentPanel.RaiseEvent(closedPanelArgs);
                view.UpdateLayout();
                Assert.True(closedPanelArgs.Handled);
                Assert.True(scrollViewer.VerticalOffset > 0);

                scrollViewer.ScrollToTop();
                view.UpdateLayout();
                Assert.Equal(0, scrollViewer.VerticalOffset);

                // When open, unhandled wheel from ComboBox, Popup child, or contentPanel must NOT scroll outer SettingsScrollViewer.
                comboBox.IsDropDownOpen = true;
                comboBox.UpdateLayout();
                Assert.True(view.IsAnyDropDownOpen);

                var popup = comboBox.Template?.FindName("PART_Popup", comboBox) as Popup;
                Assert.NotNull(popup?.Child);
                var popupArgs = CreateWheelArgs();
                popup!.Child.RaiseEvent(popupArgs);
                Assert.True(popupArgs.Handled);

                var comboArgs = CreateWheelArgs();
                comboBox.RaiseEvent(comboArgs);
                Assert.True(comboArgs.Handled);

                var panelArgs = CreateWheelArgs();
                contentPanel.RaiseEvent(panelArgs);
                Assert.True(panelArgs.Handled);
                view.UpdateLayout();

                Assert.Equal(0, scrollViewer.VerticalOffset);
                comboBox.IsDropDownOpen = false;
            }
            finally
            {
                window.Close();
            }
        });
    }

    private static MouseWheelEventArgs CreateWheelArgs() =>
        new(Mouse.PrimaryDevice, Environment.TickCount, -120)
        {
            RoutedEvent = UIElement.MouseWheelEvent
        };

    private static void RunOnStaThread(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        if (failure is not null)
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
