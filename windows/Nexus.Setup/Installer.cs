using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.Versioning;
using System.Security.Cryptography.X509Certificates;
using System.Security.Principal;
using Microsoft.Win32;

namespace Nexus.Setup;

[SupportedOSPlatform("windows")]
internal sealed class Installer(SetupOptions options, TextWriter log)
{
    private static readonly string[] Parts = ["Agent", "Tray"];
    private readonly string _system32 = Environment.SystemDirectory;
    private readonly string _programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
    private string Root => SetupPlan.InstallRoot(_programFiles);

    public int Run()
    {
        if (!new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator))
        {
            Say("Нужны права администратора: запустите установщик ещё раз и нажмите «Да» в окне Windows.");
            return 2;
        }
        return options.Uninstall ? Uninstall() : Install();
    }

    private int Install()
    {
        Say("Nexus PC — установка");
        using var payload = OpenPayload();
        if (payload is null)
        {
            Say("В этом файле нет программ для установки (собран без payload.zip). Скачайте NexusPC-Setup.exe со страницы сборки на GitHub.");
            return 3;
        }
        if (SetupPlan.CheckPayload(payload) is { } broken)
        {
            Say(broken);
            return 3;
        }
        var newVersion = ReadVersion(payload);

        var runtimes = RunQuiet(SetupPlan.ListRuntimes(_programFiles));
        var missing = runtimes.Exit == 0 ? SetupPlan.MissingRuntimes(runtimes.Output) : ["Microsoft.AspNetCore.App", "Microsoft.WindowsDesktop.App"];
        if (missing.Count > 0)
        {
            Say($"Не установлена среда .NET 10: {string.Join(", ", missing)}.");
            Say("Скачайте «.NET Desktop Runtime 10» и «ASP.NET Core Runtime 10» (Windows x64) на https://dotnet.microsoft.com/download/dotnet/10.0 и запустите установщик снова.");
            return 4;
        }

        var agentExe = SetupPlan.AgentExe(Root);
        var oldVersion = File.Exists(agentExe) ? FileVersionInfo.GetVersionInfo(agentExe).ProductVersion?.Split('+')[0] : null;
        Say(oldVersion is null ? $"Устанавливаю версию {newVersion}." : $"Обновляю {oldVersion} → {newVersion}. Сопряжённые устройства, автоматизации и настройки сохраняются.");

        Step("Останавливаю Nexus PC", () =>
        {
            StopTray();
            StopService();
        });

        Step("Копирую файлы в " + Root, () =>
        {
            var staging = Path.Combine(Path.GetTempPath(), "NexusPC-Setup-" + Guid.NewGuid().ToString("N"));
            payload.ExtractToDirectory(staging);
            try
            {
                foreach (var part in Parts)
                {
                    var target = Path.Combine(Root, part);
                    Retry(() =>
                    {
                        if (Directory.Exists(target))
                        {
                            Directory.Delete(target, recursive: true);
                        }
                    });
                    CopyDirectory(Path.Combine(staging, part), target);
                }
            }
            finally
            {
                Directory.Delete(staging, recursive: true);
            }
        });

        Step("Настраиваю службу Windows", () =>
        {
            var query = RunQuiet(SetupPlan.ScQuery(_system32));
            var exists = SetupPlan.ServiceState(query.Exit, query.Output) is not null;
            Must(exists ? SetupPlan.ScConfig(_system32, agentExe) : SetupPlan.ScCreate(_system32, agentExe));
            RunQuiet(SetupPlan.ScDescription(_system32));
            Must(SetupPlan.ScFailure(_system32));
        });

        Step("Брандмауэр: порт 47800 только для частной сети", () =>
        {
            RunQuiet(SetupPlan.FirewallDelete(_system32)); // also replaces the rule made by hand earlier
            Must(SetupPlan.FirewallAdd(_system32, agentExe));
        });

        Step("Запускаю службу", () =>
        {
            Must(SetupPlan.ScStart(_system32));
            WaitForState(4, TimeSpan.FromSeconds(30));
        });

        if (!options.NoTray)
        {
            Step("Запускаю Nexus PC в трее и включаю автозапуск", () =>
            {
                using (var run = Registry.CurrentUser.CreateSubKey(SetupPlan.RunKey))
                {
                    run.SetValue(SetupPlan.RunValue, $"\"{SetupPlan.TrayExe(Root)}\"");
                }
                // Through Explorer, so the tray runs with normal rights, not as administrator.
                using var _ = Process.Start(new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"), $"\"{SetupPlan.TrayExe(Root)}\""));
            });
        }

        CheckNetworks();

        Say("");
        Say("Готово. Nexus PC работает.");
        Say(oldVersion is null
            ? "Дальше: значок «N» у часов → «Подключить iPhone…» и отсканируйте QR-код в приложении Nexus PC."
            : "iPhone подключится сам. Если приложение было открыто — потяните главный экран вниз.");
        return 0;
    }

    private int Uninstall()
    {
        Say(options.Purge ? "Nexus PC — полное удаление (вместе с данными)" : "Nexus PC — удаление (данные и сопряжения сохраняются)");
        Step("Останавливаю Nexus PC", () =>
        {
            StopTray();
            StopService();
        });
        Step("Удаляю службу", () => RunQuiet(SetupPlan.ScDelete(_system32)));
        Step("Удаляю правило брандмауэра", () => RunQuiet(SetupPlan.FirewallDelete(_system32)));
        Step("Отключаю автозапуск", () =>
        {
            using var run = Registry.CurrentUser.OpenSubKey(SetupPlan.RunKey, writable: true);
            run?.DeleteValue(SetupPlan.RunValue, throwOnMissingValue: false);
        });
        Step("Удаляю файлы программы", () => Retry(() =>
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }));
        if (options.Purge)
        {
            Step("Удаляю данные (сопряжения, автоматизации, историю) и сертификат", () =>
            {
                var data = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "NexusPC");
                if (Directory.Exists(data))
                {
                    Directory.Delete(data, recursive: true);
                }
                using var store = new X509Store(StoreName.My, StoreLocation.LocalMachine);
                store.Open(OpenFlags.ReadWrite);
                foreach (var cert in store.Certificates.Where(c => c.Subject.StartsWith("CN=Nexus PC Agent ", StringComparison.Ordinal)).ToList())
                {
                    store.Remove(cert);
                    cert.Dispose();
                }
            });
            Say("Ваш список программ и профилей остаётся в %LOCALAPPDATA%\\NexusPC — удалите папку вручную, если не нужен.");
        }
        Say("Готово.");
        return 0;
    }

    private void CheckNetworks()
    {
        var result = RunQuiet(SetupPlan.ListNetworks(_system32));
        var publicNetworks = SetupPlan.ParseNetworks(result.Output).Where(n => n.Category == "Public").ToList();
        if (publicNetworks.Count == 0)
        {
            return;
        }
        Say("");
        Say("Эти сети Windows считает общественными — в них iPhone не подключится к ПК:");
        foreach (var network in publicNetworks)
        {
            Say($"  • {network.Name} ({network.Alias})");
            if (options.Quiet)
            {
                continue;
            }
            Console.Write("    Это ваша домашняя сеть? Сделать её частной? [д/Н]: ");
            var answer = Console.ReadLine()?.Trim().ToLowerInvariant();
            if (answer is "д" or "да" or "y" or "yes")
            {
                Must(SetupPlan.MakePrivate(_system32, network.Index));
                Say("    Сделано.");
            }
        }
        Say("В кафе и других чужих сетях оставляйте «общественную» — там ПК закрыт для подключений.");
    }

    private static void StopTray()
    {
        foreach (var process in Process.GetProcessesByName("Nexus.Tray"))
        {
            using (process)
            {
                try
                {
                    process.Kill();
                    process.WaitForExit(5000);
                }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
                {
                    // already gone
                }
            }
        }
    }

    private void StopService()
    {
        var query = RunQuiet(SetupPlan.ScQuery(_system32));
        var state = SetupPlan.ServiceState(query.Exit, query.Output);
        if (state is null or 1)
        {
            return;
        }
        RunQuiet(SetupPlan.ScStop(_system32));
        WaitForState(1, TimeSpan.FromSeconds(30));
    }

    private void WaitForState(int wanted, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var query = RunQuiet(SetupPlan.ScQuery(_system32));
            if (SetupPlan.ServiceState(query.Exit, query.Output) == wanted)
            {
                return;
            }
            Thread.Sleep(500);
        }
        throw new InvalidOperationException(wanted == 4 ? "служба не запустилась за 30 секунд" : "служба не остановилась за 30 секунд");
    }

    private static ZipArchive? OpenPayload()
    {
        var stream = typeof(Installer).Assembly.GetManifestResourceStream("payload.zip");
        return stream is null ? null : new ZipArchive(stream, ZipArchiveMode.Read);
    }

    private static string ReadVersion(ZipArchive zip)
    {
        using var reader = zip.GetEntry("version.txt") is { } entry ? new StreamReader(entry.Open()) : null;
        return reader?.ReadToEnd().Trim() is { Length: > 0 } version ? version : "dev";
    }

    private static void CopyDirectory(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
        {
            var destination = Path.Combine(target, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            Retry(() => File.Copy(file, destination, overwrite: true));
        }
    }

    private static void Retry(Action action)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                action();
                return;
            }
            catch (Exception ex) when (attempt < 10 && ex is IOException or UnauthorizedAccessException)
            {
                Thread.Sleep(1000); // a file still held by a process that is exiting
            }
        }
    }

    private void Must(Command command)
    {
        var result = RunQuiet(command);
        if (result.Exit != 0)
        {
            throw new InvalidOperationException($"{command} → код {result.Exit}: {result.Output.Trim()}");
        }
    }

    private (int Exit, string Output) RunQuiet(Command command)
    {
        var start = new ProcessStartInfo(command.File)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var arg in command.Args)
        {
            start.ArgumentList.Add(arg);
        }
        try
        {
            using var process = Process.Start(start)!;
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            process.WaitForExit(120_000);
            var output = stdout.Result + stderr.Result;
            log.WriteLine($"> {command} → {process.ExitCode}\n{output}");
            return (process.ExitCode, output);
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            log.WriteLine($"> {command} → not started: {ex.Message}");
            return (-1, ex.Message);
        }
    }

    private void Step(string title, Action action)
    {
        Console.Write($"• {title}… ");
        log.WriteLine("== " + title);
        action();
        Console.WriteLine("готово");
    }

    private void Say(string text)
    {
        Console.WriteLine(text);
        log.WriteLine(text);
    }
}
