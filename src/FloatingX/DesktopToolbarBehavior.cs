using System;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Threading;
using Cvte.EasiNote.UI.DesktopMode;

namespace FloatingX;

internal static class DesktopToolbarBehavior
{
    internal static void KeepManualCollapse(DesktopToolBarControl control, DependencyObject anchor)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var timer = (DispatcherTimer)typeof(DesktopToolBarControl).GetField("_autoHideTimer", flags)!.GetValue(control)!;
        var timeout = typeof(DesktopToolBarControl).GetMethods(flags)
            .Single(method => method.Name.StartsWith("<SetAutoHideTimer>", StringComparison.Ordinal));
        timer.Tick -= (EventHandler)timeout.CreateDelegate(typeof(EventHandler), control);
        timer.Stop();
        // Native expand animations restart this timer. Let it stop on its next
        // tick without changing the user's expanded/collapsed state.
        timer.Tick += (_, _) => timer.Stop();

        var dragging = WindowDraggingExtension.GetDragWindow(anchor);
        var collapseOnDrag = typeof(DesktopToolBarControl).GetMethod("WindowDraggingExtension_OnDragging", flags)!;
        dragging.Dragging -= (EventHandler)collapseOnDrag.CreateDelegate(typeof(EventHandler), control);
    }
}
