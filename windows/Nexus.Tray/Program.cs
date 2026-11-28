namespace Nexus.Tray;

internal static class Program
{
    /// <summary>Settings → "Restart Nexus PC": the new tray waits for the old one to exit instead of quitting.</summary>
    public const string RestartArgument = "--restart-tray";

    [STAThread]
    private static int Main(string[] args)
    {
        // E4-6: the elevated helper for "Sign in" / "Unlink" (started by the tray through UAC). It only sends one
        // request on the admin pipe and exits with a code: no tray icon, no window, never launches anything.
        if (Nexus.Core.Account.ElevatedAccountCommand.Parse(args) is { } accountOp)
        {
            return ElevatedAccountProcess.RunElevated(accountOp);
        }
        RunTray(restarting: args.Length == 1 && args[0] == RestartArgument);
        return 0;
    }

    private static void RunTray(bool restarting)
    {
        // One tray per Windows session. After a restart from Settings, wait a moment for the old tray to exit.
        using var single = new Mutex(initiallyOwned: false, @"Local\NexusPC.Tray");
        bool owned;
        try
        {
            owned = single.WaitOne(restarting ? TimeSpan.FromSeconds(15) : TimeSpan.Zero);
        }
        catch (AbandonedMutexException)
        {
            owned = true; // the previous tray ended without releasing it
        }
        if (!owned)
        {
            return;
        }
        try
        {
            ApplicationConfiguration.Initialize();
            // Apps started from a phone inherit the tray's rights. Never run elevated.
            using (var identity = System.Security.Principal.WindowsIdentity.GetCurrent())
            {
                if (new System.Security.Principal.WindowsPrincipal(identity).IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator))
                {
                    MessageBox.Show(TrayText.Get("ElevatedTrayBody"), TrayText.Get("ElevatedTrayTitle"), MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
            }
            Application.Run(new TrayContext());
        }
        finally
        {
            single.ReleaseMutex();
        }
    }
}
