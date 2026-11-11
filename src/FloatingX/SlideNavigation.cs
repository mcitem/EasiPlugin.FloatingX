using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using Microsoft.CSharp.RuntimeBinder;
using Microsoft.Win32.SafeHandles;

namespace FloatingX;

internal static class SlideNavigation
{
    internal static bool TryNavigate(IntPtr target, string processName, bool next)
    {
        string? progId = processName.ToUpperInvariant() switch
        {
            "WPP" => "KWPP.Application",
            "POWERPNT" => "PowerPoint.Application",
            _ => null
        };
        if (progId == null)
        {
            return false;
        }

        var references = new List<object>();
        object Keep(object value)
        {
            references.Add(value);
            return value;
        }

        try
        {
            var titleBuffer = new StringBuilder(1024);
            if (GetWindowText(target, titleBuffer, titleBuffer.Capacity) == 0)
            {
                return false;
            }

            string title = titleBuffer.ToString();
            bool powerPoint = progId == "PowerPoint.Application";
            object? application = null;
            try
            {
                CLSIDFromProgID(progId, out Guid clsid);
                GetActiveObject(ref clsid, IntPtr.Zero, out object activeApplication);
                application = Keep(activeApplication);
            }
            catch (Exception ex) when (powerPoint && (ex is COMException or UnauthorizedAccessException))
            {
                // PowerPoint 仍可能已将演示文稿注册到运行对象表。
            }

            if (powerPoint && (application == null || !OwnsPowerPointApplication(target, (string)((dynamic)application).Path)))
            {
                application = FindPowerPointApplication(target, title, Keep);
            }

            if (application == null)
            {
                return false;
            }

            dynamic app = application;
            dynamic windows = Keep(app.SlideShowWindows);
            int count = windows.Count;
            for (int index = 1; index <= count; index++)
            {
                dynamic window = Keep(windows.Item(index));
                if (Convert.ToInt32(window.Active) == 0)
                {
                    continue;
                }

                dynamic presentation = Keep(window.Presentation);
                string name = presentation.Name;
                // 两套放映对象都没有公开 HWND，用活动状态和窗口标题核对文档。
                if (!MatchesTitle(title, name, powerPoint))
                {
                    continue;
                }

                dynamic view = Keep(window.View);
                dynamic currentSlide = Keep(view.Slide);
                dynamic slides = Keep(presentation.Slides);
                int destination = (int)currentSlide.SlideIndex + (next ? 1 : -1);
                int slideCount = slides.Count;
                while (destination >= 1 && destination <= slideCount)
                {
                    dynamic candidate = Keep(slides.Item(destination));
                    dynamic transition = Keep(candidate.SlideShowTransition);
                    if (Convert.ToInt32(transition.Hidden) == 0)
                    {
                        if (GetForegroundWindow() != target)
                        {
                            return false;
                        }

                        view.GotoSlide(destination);
                        return true;
                    }

                    destination += next ? 1 : -1;
                }

                // 到达边界时停留在当前页，避免误结束放映。
                return true;
            }
        }
        catch (Exception ex) when (ex is COMException or RuntimeBinderException or InvalidCastException or MissingMemberException or UnauthorizedAccessException)
        {
            Trace.TraceWarning("{0} 整页翻页不可用：{1}", progId, ex);
        }
        finally
        {
            for (int index = references.Count - 1; index >= 0; index--)
            {
                if (Marshal.IsComObject(references[index]))
                {
                    Marshal.ReleaseComObject(references[index]);
                }
            }
        }

        return false;
    }

    private static object? FindPowerPointApplication(IntPtr target, string title, Func<object, object> keep)
    {
        // WPS 也可能注册 PowerPoint 的 COM 标识。通过已打开的文稿找到实际的 PowerPoint。
        GetRunningObjectTable(0, out IRunningObjectTable table);
        keep(table);
        CreateBindCtx(0, out IBindCtx context);
        keep(context);
        table.EnumRunning(out IEnumMoniker entries);
        keep(entries);
        var entry = new IMoniker[1];
        while (entries.Next(1, entry, IntPtr.Zero) == 0)
        {
            IMoniker moniker = entry[0];
            keep(moniker);
            try
            {
                moniker.GetDisplayName(context, null, out string path);
                string name = Path.GetFileName(path);
                if (name.Length == 0 || !MatchesTitle(title, name, powerPoint: true) ||
                    table.GetObject(moniker, out object document) != 0)
                {
                    continue;
                }

                dynamic presentation = keep(document);
                dynamic app = keep(presentation.Application);
                if (OwnsPowerPointApplication(target, (string)app.Path))
                {
                    return app;
                }
            }
            catch (Exception ex) when (ex is COMException or RuntimeBinderException or InvalidCastException or UnauthorizedAccessException)
            {
                // 枚举过程中其他程序可能关闭文档，继续检查下一项。
            }
        }

        return null;
    }

    private static bool OwnsPowerPointApplication(IntPtr target, string applicationPath)
    {
        GetWindowThreadProcessId(target, out uint process);
        const uint queryLimitedInformation = 0x1000;
        using var handle = OpenProcess(queryLimitedInformation, false, process);
        var imagePath = new StringBuilder(1024);
        int length = imagePath.Capacity;
        return !handle.IsInvalid && QueryFullProcessImageName(handle, 0, imagePath, ref length) &&
            string.Equals(Path.GetDirectoryName(imagePath.ToString()),
                applicationPath.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);
    }

    private static bool MatchesTitle(string title, string name, bool powerPoint) =>
        title.EndsWith("[" + name + "]", StringComparison.OrdinalIgnoreCase) ||
        (powerPoint && (title.EndsWith(" - " + name, StringComparison.OrdinalIgnoreCase) ||
                        title.EndsWith(" - " + name + " - PowerPoint", StringComparison.OrdinalIgnoreCase)));

    [DllImport("ole32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    private static extern void CLSIDFromProgID(string progId, out Guid clsid);

    [DllImport("oleaut32.dll", PreserveSig = false)]
    private static extern void GetActiveObject(ref Guid clsid, IntPtr reserved,
        [MarshalAs(UnmanagedType.IUnknown)] out object value);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr window, StringBuilder text, int count);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint process);

    [DllImport("ole32.dll", PreserveSig = false)]
    private static extern void GetRunningObjectTable(int reserved, out IRunningObjectTable table);

    [DllImport("ole32.dll", PreserveSig = false)]
    private static extern void CreateBindCtx(int reserved, out IBindCtx context);

    [DllImport("kernel32.dll")]
    private static extern SafeProcessHandle OpenProcess(uint access, bool inherit, uint process);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern bool QueryFullProcessImageName(SafeProcessHandle process, uint flags, StringBuilder path, ref int length);
}
