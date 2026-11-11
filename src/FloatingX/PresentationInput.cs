using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Forms = System.Windows.Forms;
using static FloatingX.WindowEvents;

namespace FloatingX;

internal sealed class PresentationInput : IDisposable
{
    internal const ushort LeftArrow = 0x25;
    internal const ushort RightArrow = 0x27;

    private const uint KeyboardInput = 1;
    private const uint ExtendedKey = 0x0001;
    private const uint KeyUp = 0x0002;

    private readonly WinEventCallback _foregroundChanged;
    private readonly IntPtr _hook;
    private readonly IntPtr _visibilityHook;
    private readonly SemaphoreSlim _navigation = new(1);
    private PresentationWindow? _target;
    private event Action<IntPtr>? ForegroundChanged;

    internal event Action? PresentationChanged;

    internal PresentationWindow? CurrentWindow =>
        _target is { } target && IsPresentation(target.Handle, target.ProcessId) ? target : null;

    internal static bool IsOpen(PresentationWindow target)
    {
        GetWindowThreadProcessId(target.Handle, out uint process);
        return process == target.ProcessId;
    }

    internal PresentationInput()
    {
        _foregroundChanged = OnForegroundChanged;
        _hook = SetWinEventHook(ForegroundEvent, ForegroundEvent, IntPtr.Zero,
            _foregroundChanged, 0, 0, 0);
        _visibilityHook = SetWinEventHook(ObjectDestroy, ObjectHide, IntPtr.Zero,
            _foregroundChanged, 0, 0, 0);
        if (_hook == IntPtr.Zero)
        {
            Trace.TraceWarning("无法跟踪前台放映窗口。");
        }

        RememberForeground(GetForegroundWindow());
    }

    internal async Task SendArrowAsync(ushort key, bool wholeSlide = false)
    {
        RememberForeground(GetForegroundWindow());
        if (CurrentWindow is not { } presentation)
        {
            return;
        }

        await _navigation.WaitAsync();
        try
        {
            if (CurrentWindow == presentation)
            {
                await NavigateAsync(presentation, key, wholeSlide);
            }
        }
        finally
        {
            _navigation.Release();
        }
    }

    private async Task NavigateAsync(PresentationWindow presentation, ushort key, bool wholeSlide)
    {
        await WaitForForegroundAsync(presentation.Handle);
        if (!IsCurrentTarget(presentation))
        {
            Trace.TraceWarning("放映窗口未获得键盘焦点。");
            return;
        }

        if (wholeSlide && presentation.SupportsSlideId)
        {
            if (await Task.Run(() => SlideNavigation.TryNavigate(presentation, key == RightArrow)))
            {
                PresentationChanged?.Invoke();
                return;
            }

            if (!IsCurrentTarget(presentation))
            {
                return;
            }

            Trace.TraceInformation("整页翻页接口不可用，改用方向键。");
        }

        var events = new[] { CreateKey(key, ExtendedKey), CreateKey(key, ExtendedKey | KeyUp) };
        uint sent = SendInput((uint)events.Length, events, Marshal.SizeOf<Input>());
        if (sent == 1)
        {
            SendInput(1, new[] { events[1] }, Marshal.SizeOf<Input>());
        }

        if (sent != events.Length)
        {
            Trace.TraceWarning("方向键输入发送不完整：{0}", sent);
        }

        PresentationChanged?.Invoke();
    }

    private async Task WaitForForegroundAsync(IntPtr target)
    {
        if (GetForegroundWindow() == target)
        {
            return;
        }

        var activated = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnActivated(IntPtr window)
        {
            if (window == target)
            {
                activated.TrySetResult();
            }
        }

        ForegroundChanged += OnActivated;
        try
        {
            if (SetForegroundWindow(target) && GetForegroundWindow() != target)
            {
                await activated.Task.WaitAsync(TimeSpan.FromMilliseconds(240));
            }
        }
        catch (TimeoutException)
        {
            // 调用方会在执行导航前重新核对焦点。
        }
        finally
        {
            ForegroundChanged -= OnActivated;
        }
    }

    private bool IsCurrentTarget(PresentationWindow target) =>
        GetForegroundWindow() == target.Handle && CurrentWindow == target;

    private void OnForegroundChanged(IntPtr hook, uint eventType, IntPtr window,
        int objectId, int childId, uint eventThread, uint eventTime)
    {
        if (eventType == ForegroundEvent && window == GetForegroundWindow())
        {
            RememberForeground(window);
            ForegroundChanged?.Invoke(window);
        }
        else if (objectId == 0 && childId == 0 && window == _target?.Handle &&
                 (eventType == ObjectDestroy || eventType == ObjectHide) && CurrentWindow == null)
        {
            SetTarget(null);
        }
    }

    private void RememberForeground(IntPtr window)
    {
        GetWindowThreadProcessId(window, out uint process);
        if (process == 0 || process == (uint)Environment.ProcessId)
        {
            return;
        }

        if (IsPresentation(window, process))
        {
            try
            {
                using var owner = Process.GetProcessById((int)process);
                string processName = owner.ProcessName;
                if (processName.Equals("EasiNote", StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                SetTarget(new PresentationWindow(window, process, processName));
                return;
            }
            catch (ArgumentException)
            {
                // 前台窗口所属进程已退出。
            }
            catch (InvalidOperationException)
            {
                // 前台窗口在读取进程名称前已关闭。
            }
        }

        SetTarget(null);
    }

    private void SetTarget(PresentationWindow? target)
    {
        if (_target != target)
        {
            _target = target;
            PresentationChanged?.Invoke();
        }
    }

    private static bool IsPresentation(IntPtr window, uint process)
    {
        GetWindowThreadProcessId(window, out uint actualProcess);
        if (actualProcess != process || !IsWindowVisible(window) || IsIconic(window) ||
            window == GetShellWindow() || window == GetDesktopWindow() ||
            !GetWindowRect(window, out Rect rect))
        {
            return false;
        }

        var screen = Forms.Screen.FromHandle(window).Bounds;
        return Math.Abs(rect.Left - screen.Left) <= 2 &&
               Math.Abs(rect.Top - screen.Top) <= 2 &&
               Math.Abs(rect.Right - screen.Right) <= 2 &&
               Math.Abs(rect.Bottom - screen.Bottom) <= 2;
    }

    public void Dispose()
    {
        if (_hook != IntPtr.Zero)
        {
            UnhookWinEvent(_hook);
        }
        if (_visibilityHook != IntPtr.Zero)
        {
            UnhookWinEvent(_visibilityHook);
        }
    }

    private static Input CreateKey(ushort key, uint flags) => new()
    {
        Type = KeyboardInput,
        Data = new InputData { Keyboard = new Keyboard { VirtualKey = key, Flags = flags } }
    };

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        internal int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Input
    {
        internal uint Type;
        internal InputData Data;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputData
    {
        [FieldOffset(0)] internal Keyboard Keyboard;
        [FieldOffset(0)] internal Mouse Mouse;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Keyboard
    {
        internal ushort VirtualKey, ScanCode;
        internal uint Flags, Time;
        internal UIntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Mouse
    {
        internal int X, Y;
        internal uint Data, Flags, Time;
        internal UIntPtr ExtraInfo;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr window);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint process);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr window, out Rect rect);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr window);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr window);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDesktopWindow();

    [DllImport("user32.dll")]
    private static extern IntPtr GetShellWindow();

    [DllImport("user32.dll")]
    private static extern uint SendInput(uint count, Input[] input, int size);
}
