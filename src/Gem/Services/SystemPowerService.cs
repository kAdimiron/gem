using System.Diagnostics;
using System.Runtime.InteropServices;
using Gem.Models;

namespace Gem.Services;

public interface ISystemPowerService
{
    bool Execute(PostCompletionAction action);
}

public sealed class WindowsSystemPowerService : ISystemPowerService
{
    public bool Execute(PostCompletionAction action)
    {
        try
        {
            return action switch
            {
                PostCompletionAction.None => false,
                PostCompletionAction.Sleep => SetSuspendState(false, false, false),
                PostCompletionAction.Hibernate => SetSuspendState(true, false, false),
                PostCompletionAction.Shutdown => StartShutdown(),
                _ => false
            };
        }
        catch
        {
            return false;
        }
    }

    private static bool StartShutdown()
    {
        var psi = new ProcessStartInfo("shutdown.exe", "/s /t 0")
        {
            CreateNoWindow = true,
            UseShellExecute = false
        };
        return Process.Start(psi) is not null;
    }

    [DllImport("powrprof.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetSuspendState(bool hibernate, bool forceCritical, bool disableWakeEvent);
}
