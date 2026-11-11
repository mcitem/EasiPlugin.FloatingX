using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using Cvte.EasiNote.UI.DesktopMode;
using Cvte.Paint.Framework;
using Cvte.Paint.Inks;

namespace FloatingX;

internal sealed class PresentationInk
{
    // COM 的翻页事件可能早于页切换。每个信号只启动这一组有限的确认查询。
    private static readonly int[] ConfirmDelays = { 0, 25, 75, 200, 450 };
    private readonly DesktopBoardWindow _board;
    private readonly PresentationInput _input;
    private readonly SlideNavigation.PageReader _reader;
    private readonly Dictionary<PresentationPage, Ink[]> _pages = new();
    private Ink[] _desktopInk = Array.Empty<Ink>();
    private PresentationPage? _page;
    private bool _reading;
    private bool _stopped;
    private bool _waitingForRelease;
    private int _signal;

    internal PresentationInk(DesktopBoardWindow board, PresentationInput input)
    {
        _board = board;
        _input = input;
        _reader = new SlideNavigation.PageReader(() =>
        {
            // COM 回调只投递信号，立即返回给放映程序。
            _ = board.Dispatcher.InvokeAsync(RequestRefresh, DispatcherPriority.Background);
        });
        input.PresentationChanged += RequestRefresh;
        board.IsVisibleChanged += (_, _) => RequestRefresh();
        board.AddHandler(UIElement.PreviewMouseUpEvent, new MouseButtonEventHandler((_, _) => ResumeAfterInput()), true);
        board.AddHandler(UIElement.PreviewStylusUpEvent, new StylusEventHandler((_, _) => ResumeAfterInput()), true);
        board.AddHandler(UIElement.PreviewTouchUpEvent, new EventHandler<TouchEventArgs>((_, _) => ResumeAfterInput()), true);
        board.LostMouseCapture += (_, _) => ResumeAfterInput();
        board.LostStylusCapture += (_, _) => ResumeAfterInput();
        board.LostTouchCapture += (_, _) => ResumeAfterInput();
        board.Closed += (_, _) => Stop();
        RequestRefresh();
    }

    private void ResumeAfterInput()
    {
        if (_waitingForRelease)
        {
            _ = _board.Dispatcher.InvokeAsync(RequestRefresh, DispatcherPriority.Background);
        }
    }

    private void Stop()
    {
        if (_stopped)
        {
            return;
        }

        _stopped = true;
        _input.PresentationChanged -= RequestRefresh;
        _ = Task.Run(_reader.Dispose);
        _pages.Clear();
        _desktopInk = Array.Empty<Ink>();
    }

    private async void RequestRefresh()
    {
        if (_stopped || !_board.IsVisible)
        {
            return;
        }

        _signal++;
        if (_reading)
        {
            return;
        }

        _reading = true;
        try
        {
            int signal;
            do
            {
                signal = _signal;
                await ConfirmPageAsync(signal);
            }
            while (!_stopped && _board.IsVisible && signal != _signal);
        }
        catch (Exception ex)
        {
            Trace.TraceError("无法切换放映页笔迹：{0}", ex);
            Stop();
        }
        finally
        {
            _reading = false;
        }
    }

    private async Task ConfirmPageAsync(int signal)
    {
        var target = _input.CurrentWindow;
        bool hasPresentation = target is { SupportsSlideId: true };
        foreach (int delay in ConfirmDelays)
        {
            if (delay > 0)
            {
                await Task.Delay(delay);
            }

            if (_stopped || !_board.IsVisible || signal != _signal)
            {
                return;
            }

            // 即使笔尖按着也先连接事件，等笔画结束后再替换墨迹。
            var page = await Task.Run(() => _reader.Read(target));
            if (_stopped || !_board.IsVisible || signal != _signal || target != _input.CurrentWindow)
            {
                return;
            }

            if (IsWriting())
            {
                _waitingForRelease = true;
                continue;
            }

            _waitingForRelease = false;
            if (!hasPresentation || (page.HasValue && page != _page))
            {
                if (page != _page)
                {
                    SwitchInk(page);
                }

                return;
            }
        }

        if (!_waitingForRelease && _page is { } previous && previous.Window != target)
        {
            SwitchInk(null);
        }
    }

    private bool IsWriting() =>
        _board.IsMouseCaptureWithin || _board.IsStylusCaptureWithin ||
        _board.AreAnyTouchesCapturedWithin || (GetAsyncKeyState(0x01) & 0x8000) != 0;

    private void SwitchInk(PresentationPage? nextPage)
    {
        var slide = _board.InkBoard.Slides[0];
        Ink[] current = slide.Elements.OfType<Ink>().ToArray();
        Ink[] next = nextPage is { } key
            ? _pages.GetValueOrDefault(key, Array.Empty<Ink>())
            : _desktopInk;

        try
        {
            slide.Remove(current);
            slide.Add(next);
        }
        catch
        {
            // 宿主批量加入元素可能部分成功，恢复切换前的原生笔画。
            slide.RemoveAll<Ink>();
            slide.Add(current);
            throw;
        }

        if (_page is { } oldPage)
        {
            if (current.Length > 0 && PresentationInput.IsOpen(oldPage.Window))
            {
                _pages[oldPage] = current;
            }
        }
        else
        {
            _desktopInk = current;
        }

        if (nextPage is { } activePage)
        {
            _pages.Remove(activePage);
        }
        else
        {
            _desktopInk = Array.Empty<Ink>();
        }

        _page = nextPage;
        foreach (var closedPage in _pages.Keys.Where(p => !PresentationInput.IsOpen(p.Window)).ToArray())
        {
            _pages.Remove(closedPage);
        }
    }

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int virtualKey);
}
