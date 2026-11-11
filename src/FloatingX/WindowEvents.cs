using System;
using System.Runtime.InteropServices;

namespace FloatingX;

internal static class WindowEvents
{
    internal const uint ForegroundEvent = 0x0003;
    internal const uint ObjectDestroy = 0x8001;
    internal const uint ObjectShow = 0x8002;
    internal const uint ObjectHide = 0x8003;
    internal const uint ObjectNameChange = 0x800C;

    internal delegate void WinEventCallback(IntPtr hook, uint eventType, IntPtr window,
        int objectId, int childId, uint eventThread, uint eventTime);

    [DllImport("user32.dll")]
    internal static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr module,
        WinEventCallback callback, uint process, uint thread, uint flags);

    [DllImport("user32.dll")]
    internal static extern bool UnhookWinEvent(IntPtr hook);
}
