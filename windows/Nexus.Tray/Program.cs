namespace Nexus.Tray;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        // E4-6: the elevated helper for "Sign in" / "Unlink" (started by the tray through UAC). It only sends one
        // request on the admin pipe and exits with a code: no tray icon, no window, never launches anything.
        if (Nexus.Core.Account.ElevatedAccountCommand.Parse(args) is { } accountOp)
        {
            return ElevatedAccountProcess.RunElevated(accountOp);
        }
        RunTray();
        return 0;
    }

    private static void RunTray()
    {
        // One tray per Windows session.
        using var single = new Mutex(initiallyOwned: true, @"Local\NexusPC.Tray", out var isFirst);
        if (!isFirst)
        {
            return;
        }
        ApplicationConfiguration.Initialize();
        // Apps started from a phone inherit the tray's rights. Never run elevated.
        using (var identity = System.Security.Principal.WindowsIdentity.GetCurrent())
        {
            if (new System.Security.Principal.WindowsPrincipal(identity).IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator))
            {
                MessageBox.Show(
                    "Nexus PC Tray запущен с правами администратора. Тогда программы, запущенные с телефона, тоже получили бы эти права.\n\n" +
                    "Запустите его обычным способом (из меню «Пуск» или Проводника) — без «Запуск от имени администратора».",
                    "Nexus PC", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
        }
        Application.Run(new TrayContext());
    }
}
