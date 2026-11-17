using System.Text;

namespace Nexus.Setup;

internal static class Program
{
    private const string Usage = """
        NexusPC-Setup.exe                установить или обновить Nexus PC
        NexusPC-Setup.exe --uninstall    удалить (сопряжения и настройки сохраняются)
        NexusPC-Setup.exe --purge        удалить вместе с данными и сертификатом
        Дополнительно: --quiet (без вопросов), --no-tray (не запускать трей)
        """;

    public static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        Console.InputEncoding = Encoding.UTF8;
        if (SetupOptions.Parse(args) is not { } options)
        {
            Console.WriteLine(Usage);
            return 1;
        }
        if (!OperatingSystem.IsWindows())
        {
            Console.WriteLine("Установщик работает только в Windows.");
            return 1;
        }

        var logPath = Path.Combine(Path.GetTempPath(), "NexusPC-Setup.log");
        using var log = new StreamWriter(logPath, append: false, Encoding.UTF8) { AutoFlush = true };
        int code;
        try
        {
            code = new Installer(options, log).Run();
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            Console.WriteLine();
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine("Ошибка: " + ex.Message);
            Console.ResetColor();
            Console.WriteLine("Подробности: " + logPath + " — пришлите этот файл, если не получается.");
            log.WriteLine(ex.ToString());
            code = 5;
        }
        if (!options.Quiet)
        {
            Console.WriteLine();
            Console.Write("Нажмите Enter, чтобы закрыть окно.");
            Console.ReadLine();
        }
        return code;
    }
}
