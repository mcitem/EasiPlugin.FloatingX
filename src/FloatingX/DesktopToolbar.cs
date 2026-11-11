using System;
using System.Diagnostics;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using Cvte.EasiNote;
using Cvte.EasiNote.UI.DesktopMode;
using Cvte.Windows.Input;

namespace FloatingX;

internal static class DesktopToolbar
{
    private const string Prefix = "EasiPlugin.FloatingX.Desktop.";
    private const string SettingsKey = Prefix + "Settings";
    private static DesktopModeViewModel? _model;
    private static readonly Dictionary<string, UIItem> AllTools = new(StringComparer.Ordinal);
    private static UIItem? _settingsTool;

    internal static event Action? SettingsRequested;

    internal static string GetIconKey(ButtonChoice choice) =>
        AllTools.TryGetValue(choice.Key, out var item) && item is DesktopToolBarItem { IconKey: { Length: > 0 } key }
            ? key : choice.Key switch
            {
                "SelectMode" => "Geometry.DesktopToolBar.Arrow",
                "PenMode" => "Geometry.DesktopToolBar.Pen",
                "EraserMode" => "Geometry.DesktopToolBar.Eraser",
                "BackMode" => "Geometry.DesktopToolBar.BackToEn",
                _ => choice.Key
            };

    internal sealed record ButtonChoice(string Key, string Label, string Description = "",
        string IconPath = "M 4,4 H 20 V 20 H 4 Z")
    {
        public override string ToString() => Label;
    }

    internal static List<ButtonChoice> GetButtons(bool useSavedOrder = true)
    {
        var choices = new List<ButtonChoice>
        {
            new("SelectMode", "鼠标", "操作下方的软件", "M 5,3 V 20 L 10,15 L 14,22 L 17,20 L 13,13 H 21 Z"),
            new("PenMode", "笔", "希沃原生书写", "M 5,15 L 15,5 L 19,9 L 9,19 L 4,20 Z M 16,4 L 18,2 L 22,6 L 20,8 Z"),
            new("EraserMode", "橡皮擦", "擦除桌面笔迹", "F0 M 3,13 L 13,3 L 22,12 L 12,22 H 10 Z M 6,13 L 11,18 L 15,14 L 10,9 Z"),
            new("BackMode", "恢复希沃白板", "返回全屏授课", "F0 M 3,3 H 21 V 21 H 3 Z M 5,5 V 19 H 19 V 5 Z M 9,7 H 17 V 15 H 15 V 11 L 9,17 L 7,15 L 13,9 H 9 Z"),
            new(Prefix + "Clear", "清屏", "清除全部桌面笔迹", "F0 M 5,6 H 9 V 4 H 15 V 6 H 19 V 8 H 5 Z M 7,9 H 17 L 16,20 H 8 Z M 10,11 H 11 V 17 H 10 Z M 13,11 H 14 V 17 H 13 Z"),
            new(Prefix + "Previous", "上一步", "方向键，回退动画或页面", "M 5,12 L 12,5 L 14,7 L 10,11 H 20 V 13 H 10 L 14,17 L 12,19 Z"),
            new(Prefix + "Next", "下一步", "方向键，推进动画或页面", "M 19,12 L 12,5 L 10,7 L 14,11 H 4 V 13 H 14 L 10,17 L 12,19 Z"),
            // 收短尖角，并让两枚图标在 24×24 画布上保持镜像和视觉居中。
            new(Prefix + "PreviousSlide", "上一整页", "WPS 或 PowerPoint 返回上一张幻灯片，必要时按左方向键", "M 17.76,5.3 L 11.71,11.35 L 11.71,12.65 L 17.76,18.7 L 19.46,17 L 14.46,12 L 19.46,7 Z M 9.76,5.3 L 3.71,11.35 L 3.71,12.65 L 9.76,18.7 L 11.46,17 L 6.46,12 L 11.46,7 Z"),
            new(Prefix + "NextSlide", "下一整页", "WPS 或 PowerPoint 跳到下一张幻灯片，必要时按右方向键", "M 6.24,5.3 L 12.29,11.35 L 12.29,12.65 L 6.24,18.7 L 4.54,17 L 9.54,12 L 4.54,7 Z M 14.24,5.3 L 20.29,11.35 L 20.29,12.65 L 14.24,18.7 L 12.54,17 L 17.54,12 L 12.54,7 Z")
        };
        if (_model != null)
        {
            foreach (var item in AllTools.Values)
            {
                if (choices.All(choice => choice.Key != item.Key))
                {
                    choices.Add(new ButtonChoice(item.Key, item.Key));
                }
            }
        }

        return useSavedOrder ? choices.OrderBy(choice => OrderIndex(choice.Key)).ToList() : choices;
    }

    private static int OrderIndex(string key)
    {
        int index = FloatingSettings.Current.ButtonOrder.IndexOf(key);
        return index < 0 ? int.MaxValue : index;
    }

    internal static void ApplyOrder()
    {
        if (_model == null)
        {
            return;
        }

        foreach (var item in _model.DesktopTools.Where(item => item.Key != SettingsKey))
        {
            AllTools[item.Key] = item;
        }

        var ordered = GetButtons().Where(choice => !FloatingSettings.Current.HiddenButtons.Contains(choice.Key) ||
                (choice.Key == "BackMode" && DesktopLauncher.IsStandalone))
            .Where(choice => AllTools.ContainsKey(choice.Key)).Select(choice => AllTools[choice.Key]).ToList();
        if (ordered.Count == 0 && _settingsTool != null)
        {
            // 原生展开动画需要至少一个工具；全部隐藏时保留设置入口。
            ordered.Add(_settingsTool);
        }
        for (int index = _model.DesktopTools.Count - 1; index >= 0; index--)
        {
            if (!ordered.Contains(_model.DesktopTools[index]))
            {
                _model.DesktopTools.RemoveAt(index);
            }
        }

        for (int index = 0; index < ordered.Count; index++)
        {
            int current = _model.DesktopTools.IndexOf(ordered[index]);
            if (current < 0)
            {
                _model.DesktopTools.Insert(index, ordered[index]);
            }
            else if (current != index)
            {
                _model.DesktopTools.Move(current, index);
            }
        }

        foreach (var window in Application.Current.Windows.OfType<DesktopBoardToolbarWindow>())
        {
            if (window.ContainerDesktopBar is DesktopToolBarControl { ToolOpen: true } control)
            {
                // Refresh the native expanded animation after containers reflect the new order.
                window.Dispatcher.InvokeAsync(() =>
                {
                    if (control.ToolOpen)
                    {
                        control.UpdateLayout();
                        typeof(DesktopToolBarControl).GetMethod("ShowIconPanel", BindingFlags.Instance | BindingFlags.NonPublic)!
                            .Invoke(control, null);
                    }
                }, DispatcherPriority.Loaded);
            }
        }
    }

    internal static void Register(BlankWhiteboard blankWhiteboard)
    {
        var input = new PresentationInput();
        Application.Current.Exit += (_, _) => input.Dispose();
        EventManager.RegisterClassHandler(typeof(RadioButton), FrameworkElement.LoadedEvent,
            new RoutedEventHandler(PrepareActionButton));
        EventManager.RegisterClassHandler(typeof(DesktopBoardToolbarWindow),
            FrameworkElement.LoadedEvent, new RoutedEventHandler((sender, _) =>
            {
                try
                {
                    AddTools((DesktopBoardToolbarWindow)sender, input, blankWhiteboard);
                    if (DesktopLauncher.IsStandalone)
                    {
                        SelectClickThrough((DesktopBoardToolbarWindow)sender);
                    }
                }
                catch (Exception ex)
                {
                    Trace.TraceError("无法扩展希沃桌面工具栏：{0}", ex);
                }
            }));
    }

    internal static void SelectClickThrough(DesktopBoardToolbarWindow toolbar)
    {
        toolbar.ContainerDesktopBar.SetSelectButtonIsChecked();
        // The native mode defaults to SelectMode, so checking it again may not
        // raise SelectModeChecked. Apply hit-through to the actual ink window too.
        toolbar.DesktopBoardWindow.GotoSelect();
    }

    private static void PrepareActionButton(object sender, RoutedEventArgs args)
    {
        if (sender is RadioButton { DataContext: DesktopToolBarItem { Key: "BackMode" } } backButton)
        {
            backButton.ToolTip = "恢复希沃白板";
            backButton.ToolTipOpening -= UpdateBackButtonToolTip;
            backButton.ToolTipOpening += UpdateBackButtonToolTip;
            return;
        }

        if (sender is not RadioButton { DataContext: ActionItem item } button)
        {
            return;
        }

        // 动作按钮沿用原生模板，但不加入鼠标、笔和橡皮擦的互斥组。
        button.GroupName = item.Key;
        button.ToolTip = item.Label;
        AutomationProperties.SetName(button, item.Label);
        button.Click -= ResetActionButton;
        button.Click += ResetActionButton;
    }

    private static void ResetActionButton(object sender, RoutedEventArgs args) =>
        ((RadioButton)sender).SetCurrentValue(ToggleButton.IsCheckedProperty, false);

    private static void UpdateBackButtonToolTip(object sender, ToolTipEventArgs args) =>
        ((RadioButton)sender).ToolTip = DesktopLauncher.IsStandalone ? "打开空白白板" : "恢复希沃白板";

    private static void AddTools(DesktopBoardToolbarWindow toolbar, PresentationInput input, BlankWhiteboard blankWhiteboard)
    {
        toolbar.Title = "希沃桌面批注";
        if (toolbar.ContainerDesktopBar is not FrameworkElement
            {
                DataContext: DesktopModeViewModel model
            } control ||
            control.FindName("ToolsBarItemsControl") is not ItemsControl items)
        {
            return;
        }

        if (!ReferenceEquals(_model, model))
        {
            AllTools.Clear();
            _model = model;
        }

        foreach (var item in model.DesktopTools.Where(item => item.Key != SettingsKey))
        {
            AllTools[item.Key] = item;
        }

        if (AllTools.ContainsKey(Prefix + "Clear"))
        {
            ExtendLayout(toolbar, control, items);
            ApplyOrder();
            return;
        }

        if (AllTools.TryGetValue("BackMode", out var back) &&
            back is DesktopToolBarItem { Command: { } originalCommand } backButton)
        {
            backButton.Command = new DelegateCommand(async () =>
            {
                if (DesktopLauncher.StandaloneOwner is { } owner)
                {
                    await blankWhiteboard.OpenAsync(owner, () => DesktopLauncher.StopForWhiteboard(owner));
                }
                else if (originalCommand.CanExecute(null))
                {
                    originalCommand.Execute(null);
                }
            });
        }

        AddTool("Clear", "清屏", "F0 M 5,6 H 9 V 4 H 15 V 6 H 19 V 8 H 5 Z M 7,9 H 17 L 16,20 H 8 Z M 10,11 H 11 V 17 H 10 Z M 13,11 H 14 V 17 H 13 Z",
            bar =>
            {
                bar.DesktopBoardWindow.CleanInk();
                return Task.CompletedTask;
            });
        AddTool("Previous", "上一步", "M 5,12 L 12,5 L 14,7 L 10,11 H 20 V 13 H 10 L 14,17 L 12,19 Z",
            _ => input.SendArrowAsync(PresentationInput.LeftArrow));
        AddTool("Next", "下一步", "M 19,12 L 12,5 L 10,7 L 14,11 H 4 V 13 H 14 L 10,17 L 12,19 Z",
            _ => input.SendArrowAsync(PresentationInput.RightArrow));
        foreach (var choice in GetButtons(useSavedOrder: false).Where(choice => choice.Key.EndsWith("Slide", StringComparison.Ordinal)))
        {
            bool next = choice.Key == Prefix + "NextSlide";
            AddTool(next ? "NextSlide" : "PreviousSlide", choice.Label, choice.IconPath,
                _ => input.SendArrowAsync(next ? PresentationInput.RightArrow : PresentationInput.LeftArrow, wholeSlide: true));
        }

        AddTool("Settings", "悬浮窗设置", "F0 M 3,5 H 21 V 7 H 3 Z M 3,11 H 21 V 13 H 3 Z M 3,17 H 21 V 19 H 3 Z M 7,3 H 11 V 9 H 7 Z M 14,9 H 18 V 15 H 14 Z M 6,15 H 10 V 21 H 6 Z",
            _ => { SettingsRequested?.Invoke(); return Task.CompletedTask; });
        _settingsTool = AllTools[SettingsKey];
        AllTools.Remove(SettingsKey);
        ExtendLayout(toolbar, control, items);
        ApplyOrder();
    }

    private static void ExtendLayout(DesktopBoardToolbarWindow toolbar, FrameworkElement control,
        ItemsControl items)
    {
        if (items.ReadLocalValue(FrameworkElement.MarginProperty) != DependencyProperty.UnsetValue)
        {
            return;
        }

        // 原生按钮的尺寸为 42 DIP，两侧各有 6 DIP 间距。
        Thickness originalMargin = items.Margin;
        items.Margin = originalMargin;
        DesktopToolbarBehavior.KeepManualCollapse((DesktopToolBarControl)control,
            (DependencyObject)control.FindName("AnchorBlock"));
        double expandedWidth = 0;
        double appliedExtension = 0;
        bool queued = false;

        void QueueResize()
        {
            if (queued)
            {
                return;
            }

            queued = true;
            toolbar.Dispatcher.InvokeAsync(() =>
            {
                queued = false;
                double width = toolbar.ActualWidth;
                if (!toolbar.IsVisible || width <= 0)
                {
                    return;
                }

                double scale = (control.RenderTransform as ScaleTransform)?.ScaleX ?? 1;
                int count = items.Items.Count;
                double extraToolSpace = (count - 4) * 54;
                items.Margin = new Thickness(originalMargin.Left, originalMargin.Top,
                    originalMargin.Right - extraToolSpace, originalMargin.Bottom);
                typeof(DesktopToolBarControl).GetField("_originBarWidth", BindingFlags.Instance | BindingFlags.NonPublic)
                    ?.SetValue(control, 64.0 + count * 54);
                double extension = Math.Max(0, extraToolSpace) * scale;
                if (Math.Abs(width - expandedWidth) >= 1)
                {
                    appliedExtension = 0;
                }

                double targetWidth = width + 2 * (extension - appliedExtension);
                toolbar.Left += appliedExtension - extension;
                appliedExtension = extension;
                expandedWidth = targetWidth;
                if (Math.Abs(width - targetWidth) >= 1)
                {
                    toolbar.Width = targetWidth;
                }
            }, DispatcherPriority.Loaded);
        }

        toolbar.SizeChanged += (_, args) =>
        {
            if (args.WidthChanged)
            {
                QueueResize();
            }
        };
        toolbar.IsVisibleChanged += (_, _) => QueueResize();
        ((System.Collections.Specialized.INotifyCollectionChanged)items.Items).CollectionChanged += (_, _) => QueueResize();
        QueueResize();
    }

    private static void AddTool(string id, string label, string path,
        Func<DesktopBoardToolbarWindow, Task> action)
    {
        string key = Prefix + id;
        var geometry = Geometry.Parse(path);
        geometry.Freeze();
        Application.Current.Resources[key] = geometry;
        var item = new ActionItem(label)
        {
            Key = key,
            IconKey = key,
            Command = new DelegateCommand(async () =>
            {
                try
                {
                    var bar = Application.Current.Windows.OfType<DesktopBoardToolbarWindow>()
                        .FirstOrDefault(window => window.IsVisible);
                    if (bar != null)
                    {
                        await action(bar);
                    }
                }
                catch (Exception ex)
                {
                    Trace.TraceError("桌面工具“{0}”执行失败：{1}", label, ex);
                }
            })
        };
        item.SetValue(UIItem.TextProperty, label);
        AllTools[key] = item;
    }

    private sealed class ActionItem : DesktopToolBarItem
    {
        internal ActionItem(string label) => Label = label;

        internal string Label { get; }
    }
}
