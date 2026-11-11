using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shell;
using Cvte.EasiNote;
using Cvte.EasiNote.Business;
using Cvte.EasiNote.Business.Courseware.Cloud;
using Cvte.EasiNote.Business.Courseware.Cloud.Models;
using Cvte.EasiNote.Business.DuplexInstance.Server;
using Cvte.EasiNote.Business.DuplexInstance.TransferDataObject;
using Cvte.EasiNote.UI.Styles;
using Cvte.GurnetUI.Controls;
using Cvte.Paint;
using Cvte.Paint.Framework;
using Cvte.Windows.Input;
using Cvte.Windows.Native;
using static FloatingX.WindowEvents;

namespace FloatingX;

internal sealed class BlankWhiteboard
{
    private const string ResourceKey = "EasiPlugin.FloatingX.SettingsCenter";
    private const string Title = "空白白板授课";
    internal const string FileName = "空白白板.enbx";
    internal static string TemporaryDirectory => Path.Combine(EN.Path.TempFolder, "EasiPlugin.FloatingX");
    private bool _opening;
    private string? _filePath;
    private FloatingSettingsWindow? _settingsWindow;

    internal static BlankWhiteboard Register()
    {
        var entry = new BlankWhiteboard();
        DesktopToolbar.SettingsRequested += () =>
        {
            var owner = Application.Current.Windows.OfType<Window>()
                .FirstOrDefault(window => ApplicationTabWindow.GetExtendedTitleBarArea(window) != null);
            if (owner != null)
            {
                if (!owner.IsVisible) owner.Show();
                if (owner.WindowState == WindowState.Minimized) owner.WindowState = WindowState.Normal;
                owner.Activate();
                entry.ShowSettings(owner);
            }
        };
        EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent,
            new RoutedEventHandler((sender, _) => entry.AddWindowButtons((Window)sender)));
        foreach (Window window in Application.Current.Windows)
        {
            entry.AddWindowButtons(window);
        }
        return entry;
    }

    private void AddWindowButtons(Window window)
    {
        DesktopLauncher.AddLoginButton(window);
        AddTitleBarButton(window);
    }

    private void AddTitleBarButton(Window window)
    {
        if (ApplicationTabWindow.GetExtendedTitleBarArea(window) is not StackPanel area ||
            area.Children.OfType<StackPanel>().FirstOrDefault(panel => panel.Name == "IconsStackPanel") is not { } icons ||
            icons.Children.OfType<IconButton>().Any(button => AutomationProperties.GetAutomationId(button) == ResourceKey) ||
            window.TryFindResource("Style.IconButton.System") is not Style style)
        {
            return;
        }

        var icon = Geometry.Parse(ToolbarIcons.SettingsPath);
        icon.Freeze();
        var entry = new IconButton
        {
            Style = style,
            Data = icon,
            ToolTip = "悬浮窗设置中心",
            Command = new DelegateCommand(() => ShowSettings(window))
        };
        WindowChrome.SetIsHitTestVisibleInChrome(entry, true);
        AutomationProperties.SetName(entry, "悬浮窗设置中心");
        AutomationProperties.SetAutomationId(entry, ResourceKey);
        icons.Children.Add(DesktopLauncher.CreateButton(window, style));
        icons.Children.Add(entry);
    }

    private void ShowSettings(Window owner)
    {
        if (_settingsWindow != null)
        {
            _settingsWindow.Activate();
            return;
        }

        _settingsWindow = new FloatingSettingsWindow(owner, async () => await OpenAsync(owner));
        _settingsWindow.Closed += (_, _) => _settingsWindow = null;
        _settingsWindow.Show();
    }

    internal async Task OpenAsync(Window owner, Action? onOpened = null)
    {
        if (_opening)
        {
            return;
        }

        _opening = true;
        try
        {
            if (_filePath == null || !File.Exists(_filePath))
            {
                _filePath = CreateCourseware();
            }

            var result = await CloudCoursewareOpenProvider.ActiveOrNewDisplay(
                new LocalCoursewareOpenArgument { FilePath = _filePath });
            if (!result.IsRequestSuccess)
            {
                throw new InvalidOperationException("希沃未能打开空白课件。");
            }

            if (result.ClientInfo is { } client)
            {
                if (onOpened != null)
                {
                    // The login window is minimized. Give the target Shell permission while
                    // the user's click on the desktop toolbar can still authorize it.
                    Win32.User32.AllowSetForegroundWindow((uint)client.ProcessId);
                    onOpened();
                    await ActivateDisplayAsync(client, requestDisplay: true);
                }
                else if (owner.IsActive)
                {
                    await ActivateDisplayAsync(client, owner);
                }
            }
            else
            {
                onOpened?.Invoke();
            }
        }
        catch (Exception ex)
        {
            Trace.TraceError("打开空白白板失败：{0}", ex);
            MessageBox.Show("无法打开空白白板，请稍后重试。", Title, MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            _opening = false;
        }
    }

    private static async Task ActivateDisplayAsync(ClientInfo client, Window? owner = null, bool requestDisplay = false)
    {
        using var shell = Process.GetProcessById(client.ProcessId);
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void TryActivate()
        {
            if (completed.Task.IsCompleted)
            {
                return;
            }

            try
            {
                if (owner?.IsActive == false || shell.HasExited)
                {
                    completed.TrySetResult();
                    return;
                }

                if (requestDisplay && !string.IsNullOrEmpty(client.DocumentId) && client.Mode == AppMode.Display)
                {
                    requestDisplay = false;
                    ServerProxy.Instance.Notify(client, "ActiveClient", new ShellActiveClientDto
                    {
                        RequestAppMode = AppMode.Display,
                        IsSetTopModeOnce = true
                    });
                }

                if (!requestDisplay)
                {
                    shell.Refresh();
                    IntPtr handle = shell.MainWindowHandle;
                    if (handle != IntPtr.Zero && Win32.User32.SetForegroundWindow(handle))
                    {
                        completed.TrySetResult();
                    }
                }
            }
            catch (Exception ex)
            {
                completed.TrySetException(ex);
            }
        }

        void OnFinished(object? sender, EventArgs args) => completed.TrySetResult();

        WinEventCallback windowChanged = (_, _, window, objectId, childId, _, _) =>
        {
            if (window != IntPtr.Zero && objectId == 0 && childId == 0)
            {
                TryActivate();
            }
        };
        IntPtr hook = SetWinEventHook(ObjectShow, ObjectShow, IntPtr.Zero,
            windowChanged, (uint)client.ProcessId, 0, 0);
        // 已有 Shell 可能在课件加载后更新标题，同时保持窗口可见。
        IntPtr titleHook = SetWinEventHook(ObjectNameChange, ObjectNameChange, IntPtr.Zero,
            windowChanged, (uint)client.ProcessId, 0, 0);
        try
        {
            shell.Exited += OnFinished;
            shell.EnableRaisingEvents = true;
            if (owner != null)
            {
                owner.Deactivated += OnFinished;
            }

            // 先注册事件，再检查窗口，覆盖监听建立前已经显示的情况。
            TryActivate();
            try
            {
                await completed.Task.WaitAsync(TimeSpan.FromSeconds(4));
            }
            catch (TimeoutException)
            {
                TryActivate();
                if (completed.Task.IsCompleted)
                {
                    await completed.Task;
                }
                else
                {
                    Trace.TraceWarning("等待空白白板授课窗口激活超时。");
                }
            }
        }
        finally
        {
            if (owner != null)
            {
                owner.Deactivated -= OnFinished;
            }

            shell.Exited -= OnFinished;
            if (hook != IntPtr.Zero)
            {
                UnhookWinEvent(hook);
            }
            if (titleHook != IntPtr.Zero)
            {
                UnhookWinEvent(titleHook);
            }

            GC.KeepAlive(windowChanged);
        }
    }

    private static string CreateCourseware()
    {
        const double width = 1280;
        const double height = 720;
        string slideId = Guid.NewGuid().ToString("N");
        var model = new DocumentStorageModel
        {
            Board = new BoardSaveInfo { SlideWidth = width, SlideHeight = height, SlideIds = new[] { slideId } },
            Slides = new[] { new SlideSaveInfo { Id = slideId, Width = width, Height = height } }
        };

        string directory = Path.Combine(TemporaryDirectory, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, FileName);
        EasiNoteDocumentStorage.CreateEnbxStorage().SaveAs(model, path);
        return path;
    }
}
