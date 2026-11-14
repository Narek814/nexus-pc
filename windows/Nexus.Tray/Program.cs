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
        Application.Run(new TrayContext());
    }
}
