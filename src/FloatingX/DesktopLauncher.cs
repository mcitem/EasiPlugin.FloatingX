using System;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shell;
using Cvte.Composition;
using Cvte.EasiNote;
using Cvte.EasiNote.DesktopModes;
using Cvte.EasiNote.UI;
using Cvte.EasiNote.UI.DesktopMode;
using Cvte.GurnetUI.Controls;
using Cvte.Windows.Input;

namespace FloatingX;

internal static class DesktopLauncher
{
    private const string ButtonId = "EasiPlugin.FloatingX.OpenDesktop";
    private const string Title = "打开悬浮窗";
    private static readonly Geometry MinimizeIcon = CreateMinimizeIcon();
    private static Window? _owner;
    private static WindowState _ownerState;

    internal static bool IsStandalone => _owner != null;
    internal static Window? StandaloneOwner => _owner;

    internal static void StopForWhiteboard(Window owner)
    {
        if (ReferenceEquals(_owner, owner))
        {
            Container.Current.Get<IDesktopModeProvider>().Stop();
        }
    }

    internal static void AddLoginButton(Window window)
    {
        if (!window.IsLoaded || window is not (LoginWindow or IWBLoginWindow) ||
            window.FindName("CloseButton") is not IconButton close ||
            close.Parent is not Panel panel ||
            panel.Children.OfType<IconButton>().Any(button => AutomationProperties.GetAutomationId(button) == ButtonId))
        {
            return;
        }

        var button = CreateButton(window, window.TryFindResource("Style.IconButton.System") as Style);
        button.Width = close.ActualWidth;
        button.Height = close.ActualHeight;
        if (panel is Grid)
        {
            button.HorizontalAlignment = HorizontalAlignment.Right;
            button.VerticalAlignment = VerticalAlignment.Top;
            button.Margin = new Thickness(0, close.Margin.Top, close.Margin.Right + close.ActualWidth, 0);
            Panel.SetZIndex(button, 1);
        }
        var firstSystemButton = window.FindName("MinimizeButton") is IconButton minimize &&
            ReferenceEquals(minimize.Parent, panel) ? minimize : close;
        panel.Children.Insert(panel.Children.IndexOf(firstSystemButton), button);
    }

    internal static IconButton CreateButton(Window owner, Style? style)
    {
        var button = new IconButton
        {
            Data = MinimizeIcon,
            ImageWidth = 16,
            ImageHeight = 16,
            ImageStretch = Stretch.None,
            ToolTip = Title,
            Command = new DelegateCommand(() => Open(owner))
        };
        if (style != null) button.Style = style;
        WindowChrome.SetIsHitTestVisibleInChrome(button, true);
        AutomationProperties.SetName(button, Title);
        AutomationProperties.SetAutomationId(button, ButtonId);
        return button;
    }

    private static Geometry CreateMinimizeIcon()
    {
        // The title bar draws a filled path at its original size in a 16×16 canvas.
        var path = Geometry.Parse("M14,10 L21,3 M20,10 H14 V4 M3,21 L10,14 M4,14 H10 V20");
        var stroke = new Pen(Brushes.Black, 2)
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round,
            LineJoin = PenLineJoin.Round
        };
        var icon = path.GetWidenedPathGeometry(stroke);
        icon.Transform = new ScaleTransform(16d / 24, 16d / 24);
        icon.Freeze();
        return icon;
    }

    private static void Open(Window owner)
    {
        try
        {
            var provider = Container.Current.Get<IDesktopModeProvider>();
            if (provider.IsInDesktopMode)
            {
                if (IsStandalone) ReturnToOwner();
                return;
            }

            _ownerState = owner.WindowState;
            _owner = owner;
            EN.CommandOptions.DesktopSubMode = "SelectMode";
            // Cloud owns this session. There is no teaching process to restore.
            provider.Start(new GoToDesktopModeParam
            {
                SourceProcessID = Environment.ProcessId,
                SubMode = "SelectMode",
                DisplayWindowPosition = owner.PointToScreen(new Point(owner.ActualWidth / 2, owner.ActualHeight / 2))
            });

            var toolbar = Application.Current.Windows.OfType<DesktopBoardToolbarWindow>()
                .FirstOrDefault(window => window.IsVisible);
            if (toolbar == null)
            {
                provider.Stop();
                throw new InvalidOperationException("希沃未显示桌面工具栏。");
            }
            toolbar.IsVisibleChanged += OnToolbarVisibilityChanged;
            DesktopToolbar.ApplyOrder();
            DesktopToolbar.SelectClickThrough(toolbar);
            owner.WindowState = WindowState.Minimized;
        }
        catch (Exception ex)
        {
            _owner = null;
            Trace.TraceError("打开希沃悬浮窗失败：{0}", ex);
            MessageBox.Show(owner, "无法打开希沃悬浮窗。", Title, MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private static void OnToolbarVisibilityChanged(object sender, DependencyPropertyChangedEventArgs args)
    {
        if (sender is DesktopBoardToolbarWindow { IsVisible: false } toolbar)
        {
            toolbar.IsVisibleChanged -= OnToolbarVisibilityChanged;
            _owner = null;
            DesktopToolbar.ApplyOrder();
        }
    }

    private static void ReturnToOwner()
    {
        if (_owner is not { } owner)
        {
            return;
        }

        try
        {
            Container.Current.Get<IDesktopModeProvider>().Stop();
            if (owner.IsVisible)
            {
                owner.WindowState = _ownerState;
                owner.Activate();
            }
        }
        catch (Exception ex)
        {
            Trace.TraceError("退出希沃悬浮窗失败：{0}", ex);
        }
    }
}
