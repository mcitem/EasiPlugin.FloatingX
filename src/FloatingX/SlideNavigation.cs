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

internal readonly record struct PresentationWindow(IntPtr Handle, uint ProcessId, string ProcessName)
{
    internal bool SupportsSlideId => ProcessName.Equals("WPP", StringComparison.OrdinalIgnoreCase) ||
                                     ProcessName.Equals("POWERPNT", StringComparison.OrdinalIgnoreCase);
}

internal readonly record struct PresentationPage(PresentationWindow Window, string Document, int SlideId);

internal static class SlideNavigation
{
    internal static bool TryNavigate(PresentationWindow target, bool next) =>
        IsForegroundTarget(target) && TryUseSlideShow(target, next, null, out _);

    private static bool IsForegroundTarget(PresentationWindow target)
    {
        GetWindowThreadProcessId(target.Handle, out uint process);
        return process == target.ProcessId && GetForegroundWindow() == target.Handle;
    }

    internal sealed class PageReader : IDisposable
    {
        private static readonly Guid ApplicationEvents = new("914934c2-5a91-11cf-8700-00aa0060263b");
        // EApplication: SlideShowBegin, SlideShowNextSlide, SlideShowEnd,
        // SlideShowOnNext, SlideShowOnPrevious.
        private static readonly int[] ShowEvents = { 2011, 2013, 2014, 2023, 2024 };
        private readonly object _gate = new();
        private readonly StringBuilder _titleBuffer = new(1024);
        private readonly Action<object> _showChanged;
        private readonly List<int> _subscriptions = new();
        private object? _application;
        private object? _view;
        private PresentationPage _page;
        private string? _title;
        private bool _disposed;

        internal PageReader(Action signal) => _showChanged = _ => signal();

        // 查询和释放均由后台线程执行，关闭窗口时允许尚未返回的查询先完成。
        internal PresentationPage? Read(PresentationWindow? target)
        {
            lock (_gate)
            {
                if (_disposed)
                {
                    return null;
                }

                if (target is not { SupportsSlideId: true } window ||
                    GetWindowText(window.Handle, _titleBuffer, _titleBuffer.Capacity) == 0)
                {
                    ReleaseView();
                    return null;
                }

                string title = _titleBuffer.ToString();
                if (_view != null && _page.Window == window && _title == title)
                {
                    try
                    {
                        object slide = ((dynamic)_view).Slide;
                        try
                        {
                            int id = ((dynamic)slide).SlideID;
                            return id > 0 ? _page with { SlideId = id } : null;
                        }
                        finally
                        {
                            Marshal.ReleaseComObject(slide);
                        }
                    }
                    catch (Exception ex) when (IsAutomationFailure(ex))
                    {
                        // 放映对象可能失效，重新按当前窗口查找。
                    }
                }

                ReleaseView();
                return TryUseSlideShow(window, null, this, out var page) ? page : null;
            }
        }

        internal void Bind(PresentationPage page, string title, object view, object application)
        {
            _page = page;
            _title = title;
            _view = view;
            _application = application;
            foreach (int eventId in ShowEvents)
            {
                try
                {
                    ComEventsHelper.Combine(application, ApplicationEvents, eventId, _showChanged);
                    _subscriptions.Add(eventId);
                }
                catch (Exception ex) when (IsAutomationFailure(ex) || ex is InvalidOperationException or ArgumentException)
                {
                    Trace.TraceWarning("无法订阅放映事件 {0}：{1}", eventId, ex.Message);
                }
            }
        }

        public void Dispose()
        {
            lock (_gate)
            {
                _disposed = true;
                ReleaseView();
            }
        }

        private void ReleaseView()
        {
            if (_application is { } application)
            {
                foreach (int eventId in _subscriptions)
                {
                    try
                    {
                        ComEventsHelper.Remove(application, ApplicationEvents, eventId, _showChanged);
                    }
                    catch (Exception ex) when (IsAutomationFailure(ex) || ex is InvalidOperationException or ArgumentException)
                    {
                        Trace.TraceWarning("无法解除放映事件 {0}：{1}", eventId, ex.Message);
                    }
                }

                _subscriptions.Clear();
                _application = null;
                Marshal.ReleaseComObject(application);
            }

            if (_view is { } view)
            {
                _view = null;
                Marshal.ReleaseComObject(view);
            }
        }
    }

    private static bool IsAutomationFailure(Exception ex) =>
        ex is COMException or RuntimeBinderException or InvalidCastException or MissingMemberException
            or UnauthorizedAccessException or InvalidComObjectException;

    private static bool TryUseSlideShow(PresentationWindow windowTarget, bool? next, PageReader? reader, out PresentationPage page)
    {
        page = default;
        IntPtr target = windowTarget.Handle;
        string? progId = windowTarget.ProcessName.ToUpperInvariant() switch
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
            catch (Exception ex) when (ex is COMException or UnauthorizedAccessException)
            {
                // 应用入口不可用时，仍可通过放映文稿查找运行对象。
            }

            if (application != null &&
                (!powerPoint || OwnsApplication(target, (string)((dynamic)application).Path)) &&
                TryUseApplication(application, out page))
            {
                return true;
            }

            // WPS 多实例或 PowerPoint COM 标识被 WPS 接管时，按放映标题查找文稿。
            application = FindPresentationApplication(target, title, powerPoint, Keep);
            return application != null && TryUseApplication(application, out page);

            bool TryUseApplication(dynamic app, out PresentationPage found)
            {
                found = default;
                dynamic windows = Keep(app.SlideShowWindows);
                int count = windows.Count;
                PresentationPage? matchedPage = null;
                object? matchedView = null;
                for (int index = 1; index <= count; index++)
                {
                    dynamic window = Keep(windows.Item(index));
                    if (next.HasValue && Convert.ToInt32(window.Active) == 0)
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
                    if (!next.HasValue)
                    {
                        // 浮球和笔迹窗口可能拿到焦点。按标题匹配时拒绝同名的多个放映。
                        if (matchedPage.HasValue)
                        {
                            return false;
                        }

                        string document = presentation.FullName;
                        int slideId = currentSlide.SlideID;
                        if (string.IsNullOrWhiteSpace(document) || slideId <= 0)
                        {
                            return false;
                        }

                        matchedPage = new PresentationPage(windowTarget, document, slideId);
                        matchedView = view;
                        continue;
                    }

                    dynamic slides = Keep(presentation.Slides);
                    int step = next.Value ? 1 : -1;
                    int destination = (int)currentSlide.SlideIndex + step;
                    int slideCount = slides.Count;
                    while (destination >= 1 && destination <= slideCount)
                    {
                        dynamic candidate = Keep(slides.Item(destination));
                        dynamic transition = Keep(candidate.SlideShowTransition);
                        if (Convert.ToInt32(transition.Hidden) == 0)
                        {
                            if (!IsForegroundTarget(windowTarget))
                            {
                                return false;
                            }

                            view.GotoSlide(destination);
                            return true;
                        }

                        destination += step;
                    }

                    // 到达边界时停留在当前页，避免误结束放映。
                    return true;
                }

                if (matchedPage is { } currentPage)
                {
                    found = currentPage;
                    if (reader != null && matchedView != null)
                    {
                        references.Remove(matchedView);
                        object eventSource = app;
                        references.Remove(eventSource);
                        reader.Bind(currentPage, title, matchedView, eventSource);
                    }

                    return true;
                }

                return false;
            }
        }
        catch (Exception ex) when (IsAutomationFailure(ex))
        {
            if (next.HasValue)
            {
                Trace.TraceWarning("{0} 整页翻页不可用：{1}", progId, ex);
            }
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

    private static object? FindPresentationApplication(IntPtr target, string title, bool powerPoint, Func<object, object> keep)
    {
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
                if (name.Length == 0 || !MatchesTitle(title, name, powerPoint) ||
                    table.GetObject(moniker, out object document) != 0)
                {
                    continue;
                }

                dynamic presentation = keep(document);
                dynamic app = keep(presentation.Application);
                if (OwnsApplication(target, (string)app.Path))
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

    private static bool OwnsApplication(IntPtr target, string applicationPath)
    {
        GetWindowThreadProcessId(target, out uint process);
        const uint queryLimitedInformation = 0x1000;
        using var handle = OpenProcess(queryLimitedInformation, false, process);
        var imagePath = new StringBuilder(1024);
        int length = imagePath.Capacity;
        return !handle.IsInvalid && QueryFullProcessImageName(handle, 0, imagePath, ref length) &&
            string.Equals(LongPath(Path.GetDirectoryName(imagePath.ToString())!),
                LongPath(applicationPath.TrimEnd('\\', '/')), StringComparison.OrdinalIgnoreCase);
    }

    private static string LongPath(string path)
    {
        var buffer = new StringBuilder(1024);
        uint length = GetLongPathName(path, buffer, (uint)buffer.Capacity);
        return length > 0 && length < buffer.Capacity ? buffer.ToString() : path;
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

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern uint GetLongPathName(string path, StringBuilder longPath, uint length);
}
