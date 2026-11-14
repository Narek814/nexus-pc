using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Nexus.Core.Apps;
using Nexus.Core.Power;
using Nexus.Windows;

namespace Nexus.Tray;

/// <summary>Desktop actions in the signed-in person's own session (as that person, never as SYSTEM).</summary>
internal sealed partial class WindowsDesktop : IDesktop
{
    private readonly WindowsPowerController _power = new(forceOtherSessions: false);

    public DesktopResult Launch(AppEntry app)
    {
        var start = app.Kind switch
        {
            // Packaged (Store) apps start through the shell by AppUserModelID.
            AppLaunchKind.PackagedApp => new ProcessStartInfo("explorer.exe", "shell:AppsFolder\\" + app.Target) { UseShellExecute = false },
            _ => new ProcessStartInfo(app.Target)
            {
                UseShellExecute = true, // .exe, .lnk and .url (e.g. steam://) all go through the shell
                WorkingDirectory = app.Kind == AppLaunchKind.Executable ? Path.GetDirectoryName(app.Target) ?? string.Empty : string.Empty,
            },
        };
        try
        {
            using var process = Process.Start(start);
            return DesktopResult.Success;
        }
        catch (Exception ex) when (ex is Win32Exception or FileNotFoundException or InvalidOperationException)
        {
            return new DesktopResult(false, $"Could not start {app.DisplayName}: {ex.Message}");
        }
    }

    public DesktopResult RequestClose(AppEntry app)
    {
        if (app.ProcessPath is null)
        {
            return new DesktopResult(false, "No executable is known for this app.");
        }
        var asked = 0;
        foreach (var process in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(app.ProcessPath)))
        {
            using (process)
            {
                try
                {
                    if (string.Equals(process.MainModule?.FileName, app.ProcessPath, StringComparison.OrdinalIgnoreCase)
                        && process.MainWindowHandle != IntPtr.Zero && process.CloseMainWindow())
                    {
                        asked++;
                    }
                }
                catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
                {
                    // Elevated or already exited: not ours to close.
                }
            }
        }
        return asked > 0
            ? new DesktopResult(true, $"Asked {asked} window(s) to close.")
            : new DesktopResult(false, $"{app.DisplayName} is not running (or has no window).");
    }

    public DesktopResult Lock() =>
        LockWorkStation() ? DesktopResult.Success : new DesktopResult(false, $"LockWorkStation failed ({Marshal.GetLastPInvokeError()}).");

    public PowerOutcome SchedulePower(PowerAction action, TimeSpan delay, bool force, string reason) =>
        _power.Schedule(action, delay, force, reason);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool LockWorkStation();
}
