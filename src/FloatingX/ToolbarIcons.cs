using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Markup;
using System.Windows.Media;
using Cvte.EasiNote.UI.DesktopMode;

namespace FloatingX;

// 设置页与原生桌面工具栏读取同一份几何图标和浮球标志。
internal sealed class ToolbarIcons
{
    // Nonzero fill keeps each slider knob solid where it overlaps its track.
    internal const string SettingsPath = "F1 M 3,5 H 21 V 7 H 3 Z M 3,11 H 21 V 13 H 3 Z M 3,17 H 21 V 19 H 3 Z M 7,3 H 11 V 9 H 7 Z M 14,9 H 18 V 15 H 14 Z M 6,15 H 10 V 21 H 6 Z";

    private readonly FrameworkElement _scope;
    private ResourceDictionary? _nativeIcons;
    private bool _loadAttempted;

    internal ToolbarIcons(FrameworkElement scope) => _scope = scope;

    internal ImageSource? Anchor => Find("Image.Desktop.Logo") as ImageSource;

    internal ImageSource Get(DesktopToolbar.ButtonChoice choice)
    {
        object? resource = Find(DesktopToolbar.GetIconKey(choice));
        if (resource is ImageSource image) return image;
        var geometry = resource as Geometry ?? Geometry.Parse(choice.IconPath);
        var drawing = new DrawingGroup();
        drawing.Children.Add(new GeometryDrawing(Brushes.Transparent, null, new RectangleGeometry(new Rect(0, 0, 24, 24))));
        drawing.Children.Add(new GeometryDrawing(Brushes.White, null, geometry));
        var result = new DrawingImage(drawing);
        if (result.CanFreeze) result.Freeze();
        return result;
    }

    private object? Find(string key)
    {
        foreach (var bar in Application.Current.Windows.OfType<DesktopBoardToolbarWindow>())
        {
            if (bar.ContainerDesktopBar is FrameworkElement control && control.TryFindResource(key) is { } resource)
                return resource;
        }
        if (_scope.TryFindResource(key) is { } shared) return shared;
        if (!_loadAttempted)
        {
            _loadAttempted = true;
            try
            {
                // 设置可以先于桌面浮球打开，此时读取当前宿主自带的同一个资源字典。
                _nativeIcons = new ResourceDictionary
                {
                    Source = new Uri("/EasiNote.UI;component/DesktopMode/PerformanceDesktopMode_/Business/DesktopResources/Image.DesktopToolBar.xaml", UriKind.Relative)
                };
            }
            catch (Exception ex) when (ex is IOException or XamlParseException)
            {
                Trace.TraceWarning("无法读取原生浮球图标：{0}", ex);
            }
        }
        return _nativeIcons?.Contains(key) == true ? _nativeIcons[key] : null;
    }
}
