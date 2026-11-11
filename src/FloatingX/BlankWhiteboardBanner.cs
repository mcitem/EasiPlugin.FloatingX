using System;
using System.Windows;
using System.Windows.Media;
using Cvte.EasiNote.UI;

namespace FloatingX;

internal static class BlankWhiteboardBanner
{
    internal static void Register()
    {
        // EasiNote.Extension also declares AuthTip. The display window uses the EasiNote.UI type.
        if (typeof(LoginWindow).Assembly.GetType("Cvte.EasiNote.UI.AuthTip") is { } bannerType)
        {
            EventManager.RegisterClassHandler(bannerType, FrameworkElement.LoadedEvent,
                new RoutedEventHandler((sender, _) => Hide((FrameworkElement)sender)));
            foreach (Window window in Application.Current.Windows)
            {
                TrackExistingBanners(window, bannerType);
            }
        }
    }

    private static void TrackExistingBanners(DependencyObject element, Type bannerType)
    {
        if (bannerType.IsInstanceOfType(element) && element is FrameworkElement { IsLoaded: true } banner)
        {
            Hide(banner);
            return;
        }

        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(element); index++)
        {
            TrackExistingBanners(VisualTreeHelper.GetChild(element, index), bannerType);
        }
    }

    private static void Hide(FrameworkElement banner) =>
        banner.SetCurrentValue(UIElement.VisibilityProperty, Visibility.Collapsed);
}
