using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Forms = System.Windows.Forms;

namespace FloatingX;

internal sealed class PresentationInput : IDisposable
{
    internal const ushort LeftArrow = 0x25;
    internal const ushort RightArrow = 0x27;

    private const uint ForegroundEvent = 0x0003;
    private const uint KeyboardInput = 1;
    private const uint ExtendedKey = 0x0001;
    private const uint KeyUp = 0x0002;

    private readonly WinEventCallback _foregroundChanged;
    private readonly IntPtr _hook;
    private IntPtr _target;
    private uint _targetProcess;

    internal PresentationInput()
    {
        _foregroundChanged = OnForegroundChanged;
        _hook = SetWinEventHook(ForegroundEvent, ForegroundEvent, IntPtr.Zero,
            _foregroundChanged, 0, 0, 0);
        if (_hook == IntPtr.Zero)
        {
            Trace.TraceWarning("无法跟踪前台放映窗口。");
        }

        RememberForeground(GetForegroundWindow());
    }

    internal async Task SendArrowAsync(ushort key, bool wholeSlide = false)
    {
        IntPtr foreground = GetForegroundWindow();
        RememberForeground(foreground);
        IntPtr target = _target;
        uint process = _targetProcess;
        if (target == IntPtr.Zero || !IsPresentation(target, process))
        {
            return;
        }

        if (foreground != target && SetForegroundWindow(target))
        {
            // 跨进程激活由目标窗口线程处理，返回时键盘焦点可能仍在浮球。
            for (int attempt = 0; attempt < 12 && GetForegroundWindow() != target; attempt++)
            {
                await Task.Delay(20);
            }
        }

        if (!IsCurrentTarget(target, process))
        {
            Trace.TraceWarning("放映窗口未获得键盘焦点。");
            return;
        }

        if (wholeSlide)
        {
            try
            {
                using var owner = Process.GetProcessById((int)process);
                if (SlideNavigation.TryNavigate(target, owner.ProcessName, key == RightArrow))
                {
                    return;
                }
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
            {
                // 放映进程可能已关闭，发送方向键前会再次校验目标。
            }

            if (!IsCurrentTarget(target, process))
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
    }

    private bool IsCurrentTarget(IntPtr target, uint process) =>
        GetForegroundWindow() == target && _target == target && IsPresentation(target, process);

    private void OnForegroundChanged(IntPtr hook, uint eventType, IntPtr window,
        int objectId, int childId, uint eventThread, uint eventTime)
    {
        if (window == GetForegroundWindow())
        {
            RememberForeground(window);
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
                if (owner.ProcessName.Equals("EasiNote", StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                _target = window;
                _targetProcess = process;
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

        _target = IntPtr.Zero;
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
    }

    private static Input CreateKey(ushort key, uint flags) => new()
    {
        Type = KeyboardInput,
        Data = new InputData { Keyboard = new Keyboard { VirtualKey = key, Flags = flags } }
    };

    private delegate void WinEventCallback(IntPtr hook, uint eventType, IntPtr window,
        int objectId, int childId, uint eventThread, uint eventTime);

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
    private static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr module,
        WinEventCallback callback, uint process, uint thread, uint flags);

    [DllImport("user32.dll")]
    private static extern bool UnhookWinEvent(IntPtr hook);

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
