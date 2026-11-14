namespace Nexus.Tray;

internal static class Program
{
    [STAThread]
    private static void Main()
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
