using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Cryptography.X509Certificates;
using System.Security.Principal;
using Microsoft.Win32;
using Nexus.Core.Storage;
using Nexus.Windows;

namespace Nexus.Setup;

[SupportedOSPlatform("windows")]
internal sealed class Installer(SetupOptions options, TextWriter log)
{
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
        if (options.DataDirectory is { } command)
        {
            return DataDirectoryCommand.Run(command, new InstallerConsole(log)); // takes the protected lock itself
        }
        // Installation and removal hold the same protected lock as the datadir commands for their whole run (N2):
        // none of them can work on the data folder or the service while another one does.
        return ExclusiveRun.Run(new ProtectedDataDirectoryLock(), text => Say(SetupPlan.LockRefusalText(text)), SetupPlan.ExitLockedByAnotherRun,
            () => options.Uninstall ? Uninstall() : Install());
    }

    private int Install()
    {
        if (!options.Quiet)
        {
            Banner();
        }
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

        // Before anything else is changed on disk (and before the service is stopped).
        using (var report = new StringWriter())
        {
            var refused = DataDirectoryGate(DataDirectory, report);
            if (refused is { } code)
            {
                Say(report.ToString().TrimEnd());
                return code;
            }
        }

        var agentExe = SetupPlan.AgentExe(Root);
        var oldVersion = File.Exists(agentExe) ? FileVersionInfo.GetVersionInfo(agentExe).ProductVersion?.Split('+')[0] : null;
        Say(oldVersion is null ? $"Устанавливаю версию {newVersion}." : $"Обновляю {oldVersion} → {newVersion}. Сопряжённые устройства, автоматизации и настройки сохраняются.");

        Step("Останавливаю Nexus PC", () =>
        {
            StopTray();
            StopService();
            StopLeftoverAgents();
            StopBundledOpenRgb();
        });
        WarnAboutStandaloneOpenRgb();

        Step("Копирую файлы в " + Root, () =>
        {
            var staging = Path.Combine(Path.GetTempPath(), "NexusPC-Setup-" + Guid.NewGuid().ToString("N"));
            payload.ExtractToDirectory(staging);
            try
            {
                foreach (var part in SetupPlan.Parts.Where(p => Directory.Exists(Path.Combine(staging, p))))
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

        if (payload.GetEntry(SetupPlan.PawnIoSetup) is { } pawnIo)
        {
            Step("Драйвер PawnIO (температура процессора, подсветка платы и памяти)", () => InstallPawnIo(pawnIo));
        }
        if (File.Exists(SetupPlan.OpenRgbExe(Root)))
        {
            Step("Настройки подсветки (OpenRGB)", ImportOpenRgbSettings);
        }

        Step("Настраиваю службу Windows", () =>
        {
            var exists = RunQuiet(SetupPlan.ScQuery(_system32)).Exit != 1060; // ERROR_SERVICE_DOES_NOT_EXIST
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

        Step("Добавляю «Nexus PC» в меню «Пуск»", CreateStartMenuShortcut);

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
        Write("  ✔ ", ConsoleColor.Green);
        Say("Готово. Nexus PC работает.");
        Say(oldVersion is null
            ? "Дальше: значок «N» у часов → «Подключить iPhone…» и отсканируйте QR-код в приложении Nexus PC."
            : "iPhone подключится сам. Если приложение было открыто — потяните главный экран вниз.");
        return 0;
    }

    private int Uninstall()
    {
        Say(options.Purge ? "Nexus PC — полное удаление (вместе с данными)" : "Nexus PC — удаление (данные и сопряжения сохраняются)");
        // Removing the service in the middle of a repair would leave nothing to finish it with (and --purge would
        // take the folder the journal refers to). Refuse, changing nothing, until the repair is finished.
        if (UnfinishedRepairBlocksUninstall(DataDirectory) is { } blocked)
        {
            Say(blocked);
            return SetupPlan.ExitDataDirectoryUntrusted;
        }
        Step("Останавливаю Nexus PC", () =>
        {
            StopTray();
            StopService();
        });
        Step("Закрываю оставшиеся процессы агента", () =>
        {
            StopLeftoverAgents();
            StopBundledOpenRgb();
        });
        Step("Удаляю службу", () => RunQuiet(SetupPlan.ScDelete(_system32)));
        Step("Удаляю правило брандмауэра", () => RunQuiet(SetupPlan.FirewallDelete(_system32)));
        Step("Отключаю автозапуск", () =>
        {
            using var run = Registry.CurrentUser.OpenSubKey(SetupPlan.RunKey, writable: true);
            run?.DeleteValue(SetupPlan.RunValue, throwOnMissingValue: false);
        });
        Step("Удаляю ярлык из меню «Пуск»", () =>
        {
            var shortcut = SetupPlan.StartMenuShortcut(Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms));
            if (File.Exists(shortcut))
            {
                File.Delete(shortcut);
            }
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
            var backups = SetupPlan.RemainingBackups(DataDirectory);
            if (backups.Count > 0)
            {
                Say("Резервные копии папки данных не удаляются автоматически. Остались (удалите вручную, если не нужны):");
                foreach (var backup in backups)
                {
                    Say("  " + DataText.Clean(backup, 400));
                }
            }
        }
        Say("Драйвер PawnIO не удаляется: им могут пользоваться другие программы. Удалить: Параметры → Приложения → PawnIO.");
        Say("Готово.");
        return 0;
    }

    /// <summary>"Nexus PC" in the Start menu (to start the tray by hand), via the Windows Script Host shell object.</summary>
    private void CreateStartMenuShortcut()
    {
        var path = SetupPlan.StartMenuShortcut(Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms));
        try
        {
            var shellType = Type.GetTypeFromProgID("WScript.Shell") ?? throw new InvalidOperationException("WScript.Shell");
            dynamic shell = Activator.CreateInstance(shellType)!;
            try
            {
                dynamic link = shell.CreateShortcut(path);
                link.TargetPath = SetupPlan.TrayExe(Root);
                link.WorkingDirectory = Path.GetDirectoryName(SetupPlan.TrayExe(Root));
                link.Description = "Nexus PC — управление ПК с iPhone";
                link.Save();
                System.Runtime.InteropServices.Marshal.FinalReleaseComObject(link);
            }
            finally
            {
                System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shell);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException or UnauthorizedAccessException)
        {
            log.WriteLine("Start menu shortcut not created: " + ex.Message); // optional: the tray autostarts anyway
        }
    }

    private static string DataDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "NexusPC");

    /// <summary>Silent install of the driver, unless it is already there (its installer refuses a second install).</summary>
    private void InstallPawnIo(ZipArchiveEntry entry)
    {
        using (var key = Registry.LocalMachine.OpenSubKey(SetupPlan.PawnIoUninstallKey))
        {
            if (key is not null)
            {
                Write("уже установлен — ", ConsoleColor.DarkGray);
                return;
            }
        }
        var folder = Path.Combine(Path.GetTempPath(), "NexusPC-PawnIO-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var setup = Path.Combine(folder, "PawnIO_setup.exe");
            entry.ExtractToFile(setup);
            var result = RunQuiet(SetupPlan.InstallPawnIo(setup));
            if (result.Exit != 0)
            {
                // Not fatal: everything else works; CPU temperature and some lighting show "unavailable".
                Write($"не установился (код {result.Exit}); температура процессора и подсветка платы будут недоступны — ", ConsoleColor.Yellow);
            }
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    /// <summary>
    /// Zone sizes, settings and profiles from a standalone OpenRGB of the person running the installer
    /// (%APPDATA%\OpenRGB) go to the copy the service runs, once; plugins never do.
    /// </summary>
    private void ImportOpenRgbSettings()
    {
        // Runs only after DataDirectoryGate accepted (or created) the data folder.
        var source = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "OpenRGB");
        var target = Path.Combine(DataDirectory, "OpenRGB");
        var files = SetupPlan.OpenRgbFilesToImport(source, target);
        var fs = new DataDirectoryFileSystem();
        foreach (var relative in files)
        {
            var destination = Path.Combine(target, relative);
            CreateOwnedDirectories(fs, DataDirectory, Path.GetDirectoryName(destination)!);
            // Every imported file is a new file owned by Administrators (not by the account running the
            // installer), access inherited, put in place by rename: an existing file is replaced, not rewritten.
            var temp = destination + ".import-tmp";
            if (File.Exists(temp))
            {
                File.Delete(temp);
            }
            using (var input = File.OpenRead(Path.Combine(source, relative)))
            using (var output = fs.CreateFile(temp))
            {
                input.CopyTo(output);
                fs.FlushToDisk(output);
            }
            File.Move(temp, destination, overwrite: true);
        }
        log.WriteLine($"OpenRGB settings imported: {files.Count} file(s) from {source}");
        // The folder must still pass the start-up check of the service after the import.
        using var check = new StringWriter();
        if (DataDirectoryGate(DataDirectory, check) is not null)
        {
            log.WriteLine(check.ToString());
            throw new InvalidOperationException("после переноса настроек OpenRGB папка данных не прошла проверку: " + check.ToString().Trim());
        }
        if (files.Count > 0)
        {
            Write("перенесены из вашего OpenRGB — ", ConsoleColor.DarkGray);
        }
    }

    private static void CreateOwnedDirectories(DataDirectoryFileSystem fs, string root, string directory)
    {
        if (Directory.Exists(directory) || DataDirectoryPolicy.SamePath(directory, root))
        {
            return;
        }
        CreateOwnedDirectories(fs, root, Path.GetDirectoryName(directory)!);
        fs.CreateDirectory(directory);
    }

    /// <summary>
    /// The data folder before the installer writes anything: missing — created with owner Administrators and
    /// a protected SYSTEM/Administrators DACL in one call (ERROR_ALREADY_EXISTS means it is there, not that it
    /// is fine); present — the same handle-based check as the service (SYSTEM, Administrators, TrustedInstaller
    /// only, no links, no hard links, no non-empty OpenRGB plugins). Null to go on; otherwise the exit code,
    /// with nothing changed. Shared code: Nexus.Core.Storage and Nexus.Windows, no copy here.
    /// </summary>
    internal static int? DataDirectoryGate(string path, TextWriter output)
    {
        var error = DataDirectoryFileSystem.TryCreateProtectedDirectory(path, withOwner: true);
        if (error == 0)
        {
            return null;
        }
        if (!DataDirectoryFileSystem.IsAlreadyExists(error))
        {
            throw new IOException($"Не удалось создать папку данных {path}: {new System.ComponentModel.Win32Exception(error).Message}");
        }
        DataScan scan;
        IReadOnlyList<UnfinishedRepair> unfinished;
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        using (DataRepairEnvironment.EnablePrivileges(DataRepairEnvironment.ReadPrivileges))
        {
            var fs = new DataDirectoryFileSystem();
            scan = DataDirectoryScanner.Scan(fs, root, DataDirectoryPolicy.TrustedSids);
            unfinished = [.. new DataDirectoryRepair(fs, new NullConsole(), TimeProvider.System).FindUnfinished(root).Where(r => !r.Foreign)];
        }
        if (scan.Trusted && unfinished.Count == 0)
        {
            return null;
        }
        if (unfinished.Count > 0)
        {
            output.WriteLine($"Восстановление папки данных {path} не завершено. Установка остановлена, ничего не изменено.");
            foreach (var repair in unfinished)
            {
                var backup = DataText.Clean(repair.Backup, 400);
                if (repair.State == RepairJournal.Restored)
                {
                    output.WriteLine($"  Права возвращены, восстановление не завершено ({backup}).");
                    output.WriteLine("  Завершить:       NexusPC-Setup.exe --datadir-repair (новое исправление)");
                    output.WriteLine($"  или, если папка исправлена вручную: NexusPC-Setup.exe --datadir-finalize \"{backup}\"");
                    continue;
                }
                if (repair.State is DataDirectoryRepair.Tampered or DataDirectoryRepair.TooManyBackups)
                {
                    output.WriteLine($"  {backup}: {DataText.Clean(repair.Detail, 400)}");
                    output.WriteLine("  Что делать: " + DataDirectoryRepair.FinishCommand(repair) + " (docs/SETUP.md, «Восстановление папки данных»).");
                    continue;
                }
                if (repair.State == RepairJournal.RestoreFailed)
                {
                    output.WriteLine($"  Повторить возврат прав: NexusPC-Setup.exe --datadir-restore-acl \"{backup}\"");
                    continue;
                }
                output.WriteLine($"  Продолжить:      NexusPC-Setup.exe --datadir-repair --continue \"{backup}\"");
                output.WriteLine($"  Вернуть права:   NexusPC-Setup.exe --datadir-restore-acl \"{backup}\"");
            }
            output.WriteLine("Команды выполняются в командной строке от имени администратора. Подробно: docs/SETUP.md, «Восстановление папки данных».");
            return SetupPlan.ExitDataDirectoryUntrusted;
        }
        output.WriteLine($"Папка данных Nexus PC {path} защищена не так, как нужно. Установка остановлена, ничего не изменено.");
        output.WriteLine("Найдено:");
        foreach (var violation in scan.Violations.Take(50))
        {
            output.WriteLine("  " + violation);
        }
        if (scan.Violations.Count > 50)
        {
            output.WriteLine($"  … и ещё {scan.Violations.Count - 50}");
        }
        output.WriteLine("Что делать: откройте командную строку от имени администратора, остановите службу Nexus PC, если она есть, и выполните");
        output.WriteLine("  NexusPC-Setup.exe --datadir-check    — подробный отчёт, ничего не меняет;");
        output.WriteLine("  NexusPC-Setup.exe --datadir-repair   — резервная копия, затем исправление прав.");
        output.WriteLine(scan.Repairable
            ? "Потом запустите установщик ещё раз. Подробно: docs/SETUP.md, «Восстановление папки данных»."
            : $"Автоматическое исправление откажется: в папке найдены {string.Join(", ", DataDirectoryRepair.BlockingKinds(scan).Select(SetupPlan.BlockingKindText))}. Разберите это вручную (docs/SETUP.md, «Восстановление папки данных»).");
        return SetupPlan.ExitDataDirectoryUntrusted;
    }

    /// <summary>Null when no repair of the data folder is unfinished; otherwise what to tell the person (nothing is changed).</summary>
    internal static string? UnfinishedRepairBlocksUninstall(string dataDirectory)
    {
        if (!Directory.Exists(dataDirectory))
        {
            return null;
        }
        IReadOnlyList<UnfinishedRepair> unfinished;
        using (DataRepairEnvironment.EnablePrivileges(DataRepairEnvironment.ReadPrivileges))
        {
            var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(dataDirectory));
            unfinished = [.. new DataDirectoryRepair(new DataDirectoryFileSystem(), new NullConsole(), TimeProvider.System).FindUnfinished(root).Where(r => !r.Foreign)];
        }
        if (unfinished.Count == 0)
        {
            return null;
        }
        var text = new System.Text.StringBuilder();
        text.AppendLine("Удаление остановлено, ничего не изменено: восстановление папки данных не завершено.");
        foreach (var repair in unfinished)
        {
            text.AppendLine($"  {DataText.Clean(repair.Backup, 400)} ({DataText.Clean(repair.State)})");
            text.AppendLine("  Завершить: " + SetupPlan.InstallerFinishText(repair));
        }
        text.Append("Затем запустите удаление ещё раз. Подробно: docs/SETUP.md, «Восстановление папки данных».");
        return text.ToString();
    }

    private sealed class NullConsole : IRepairConsole
    {
        public void WriteLine(string text)
        {
        }

        public string? ReadLine() => null;
    }

    private sealed class InstallerConsole(TextWriter log) : IRepairConsole
    {
        public void WriteLine(string text)
        {
            Console.WriteLine(text);
            log.WriteLine(text);
        }

        public string? ReadLine() => Console.ReadLine();
    }

    /// <summary>The OpenRGB copy in Program Files\NexusPC (the service's server, or a window left open): its DLLs would be locked.</summary>
    private void StopBundledOpenRgb()
    {
        foreach (var process in OpenRgbProcesses(ours: true))
        {
            using (process)
            {
                try
                {
                    log.WriteLine($"Stopping bundled OpenRGB pid {process.Id}");
                    process.Kill(entireProcessTree: true);
                    process.WaitForExit(10_000);
                }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
                {
                    // already exited
                }
            }
        }
    }

    /// <summary>A separately installed OpenRGB would take the lighting devices and the SDK port.</summary>
    private void WarnAboutStandaloneOpenRgb()
    {
        var others = OpenRgbProcesses(ours: false);
        if (others.Count == 0)
        {
            return;
        }
        foreach (var process in others)
        {
            process.Dispose();
        }
        Say("");
        Say("Запущен отдельно установленный OpenRGB. Теперь OpenRGB входит в Nexus PC и запускается сам,");
        Say("поэтому закройте свой OpenRGB, уберите его из автозапуска или удалите (Параметры → Приложения).");
        Say("Пока он работает, Nexus PC пользуется им.");
        Say("");
    }

    private List<Process> OpenRgbProcesses(bool ours)
    {
        var folder = Path.Combine(Root, "OpenRGB") + Path.DirectorySeparatorChar;
        var found = new List<Process>();
        foreach (var process in Process.GetProcessesByName("OpenRGB"))
        {
            string? path = null;
            try
            {
                path = process.MainModule?.FileName;
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                // exited or inaccessible
            }
            if (path is not null && path.StartsWith(folder, StringComparison.OrdinalIgnoreCase) == ours)
            {
                found.Add(process);
            }
            else
            {
                process.Dispose();
            }
        }
        return found;
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

    /// <summary>
    /// Nexus.Agent.exe processes still running from the install folder after the service stopped: an admin
    /// console ("Nexus.Agent.exe pair"/"status") or a service process that has not exited yet. They keep the
    /// DLLs locked, so files cannot be replaced (found on the owner's PC).
    /// </summary>
    private void StopLeftoverAgents()
    {
        var folder = Path.Combine(Root, "Agent") + Path.DirectorySeparatorChar;
        foreach (var process in Process.GetProcessesByName("Nexus.Agent"))
        {
            using (process)
            {
                try
                {
                    var path = process.MainModule?.FileName;
                    if (path is null || !path.StartsWith(folder, StringComparison.OrdinalIgnoreCase))
                    {
                        continue; // a development build elsewhere: not ours to stop
                    }
                    log.WriteLine($"Stopping leftover Nexus.Agent.exe pid {process.Id}");
                    process.Kill();
                    process.WaitForExit(15_000);
                }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
                {
                    // already exited
                }
            }
        }
    }

    private void StopService()
    {
        var query = RunQuiet(SetupPlan.ScQuery(_system32));
        if (query.Exit == 1060 || SetupPlan.ServiceState(query.Exit, query.Output) == 1)
        {
            return; // not installed, or already stopped
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
        var last = RunQuiet(SetupPlan.ScQuery(_system32));
        throw new InvalidOperationException((wanted == 4 ? "служба не запустилась за 30 секунд" : "служба не остановилась за 30 секунд")
            + (SetupPlan.ServiceState(last.Exit, last.Output) is { } state ? $" (состояние {state})." : ". Состояние неизвестно: " + last.Output.Trim()));
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
            catch (Exception ex) when (attempt < 20 && ex is IOException or UnauthorizedAccessException)
            {
                Thread.Sleep(1000); // a file still held by a process that is exiting
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new IOException(ex.Message + " Файл занят другой программой. Закройте окна PowerShell, где запущен Nexus.Agent.exe, и запустите установщик ещё раз.", ex);
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
        Write("  ▸ ", ConsoleColor.Magenta);
        Console.Write($"{title}… ");
        log.WriteLine("== " + title);
        action();
        Write("готово", ConsoleColor.Green);
        Console.WriteLine();
    }

    private static void Write(string text, ConsoleColor color)
    {
        var previous = Console.ForegroundColor;
        Console.ForegroundColor = color;
        Console.Write(text);
        Console.ForegroundColor = previous;
    }

    private static void Banner()
    {
        Console.WriteLine();
        Write("  ███╗   ██╗███████╗██╗  ██╗██╗   ██╗███████╗\n", ConsoleColor.Magenta);
        Write("  ████╗  ██║██╔════╝╚██╗██╔╝██║   ██║██╔════╝\n", ConsoleColor.Magenta);
        Write("  ██╔██╗ ██║█████╗   ╚███╔╝ ██║   ██║███████╗\n", ConsoleColor.DarkMagenta);
        Write("  ██║╚██╗██║██╔══╝   ██╔██╗ ██║   ██║╚════██║\n", ConsoleColor.Cyan);
        Write("  ██║ ╚████║███████╗██╔╝ ██╗╚██████╔╝███████║\n", ConsoleColor.Cyan);
        Write("  ╚═╝  ╚═══╝╚══════╝╚═╝  ╚═╝ ╚═════╝ ╚══════╝  PC\n", ConsoleColor.DarkCyan);
        Console.WriteLine();
    }

    private void Say(string text)
    {
        Console.WriteLine(text);
        log.WriteLine(text);
    }
}
