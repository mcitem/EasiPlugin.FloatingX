using System.Threading.Tasks;
using System.Windows;
using Cvte.EasiNote;

namespace FloatingX;

internal sealed class Program : dotnetCampus.EasiPlugins.EasiPlugin
{
    protected override async Task OnRunningAsync()
    {
        // 授课 Shell 通过 GoToDesktopMode 把桌面批注交给 Cloud 进程。
        if (EN.CommandOptions.IsCloud)
        {
            await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                DesktopToolbar.Register(BlankWhiteboard.Register());
            });
        }
        else
        {
            await Application.Current.Dispatcher.InvokeAsync(BlankWhiteboardBanner.Register);
            await BlankWhiteboardSyncGuard.RegisterAsync();
        }
    }
}
