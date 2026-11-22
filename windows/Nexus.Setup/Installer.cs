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
internal sealed class Installer(SetupOptions options, TextWriter log) : IInstallServices
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

        // T5 sections 1 and 2 (stage U1): unpack into a checked, protected staging folder in Program Files (not %TEMP%) and
        // verify every written file against manifest.json, all before the service is stopped or anything installed is changed.
        var manifest = ReadManifest(payload);
        if (manifest is null)
        {
            Say(SetupPlan.ManifestBrokenText);
            return 3;
        }
        if (PayloadManifest.CheckArchive(payload, manifest) is { } mismatch)
        {
            log.WriteLine("manifest: " + mismatch);
            Say(SetupPlan.ManifestBrokenText);
            return 3;
        }
        var fs = new DataDirectoryFileSystem();
        var required = PayloadManifest.RequiredFreeBytes(manifest);
        if (fs.AvailableBytes(_programFiles) < required)
        {
            Say(SetupPlan.NotEnoughSpaceText(required));
            return 3;
        }
        // T5 section 3: an interrupted earlier run is finished or undone first, from its journal — only after the
        // install root itself passed the path checks (nothing is moved inside a folder others could change).
        if (Directory.Exists(Root))
        {
            try
            {
                StagingDirectory.CheckInstallRoot(Root);
            }
            catch (StagingRefusedException refused)
            {
                log.WriteLine("install root refused: " + refused.Message);
                Say(SetupPlan.UnsafeInstallPathText(Root));
                return SetupPlan.ExitUnsafeInstallPath;
            }
            var recovery = new InstallTransaction(new WindowsInstallFileSystem(), this, Root, log).Recover();
            log.WriteLine("Recovery of earlier runs: " + recovery);
            if (recovery == RecoveryResult.Failed)
            {
                Say(SetupPlan.RecoveryFailedText);
                return SetupPlan.ExitRecoveryIncomplete;
            }
            if (recovery == RecoveryResult.JournalUnreadable)
            {
                Say(SetupPlan.JournalUnreadableText(InstallJournal.PathIn(Root)));
                return SetupPlan.ExitRecoveryIncomplete;
            }
        }
        StagingDirectory staging;
        try
        {
            staging = StagingDirectory.Create(Root, log);
        }
        catch (StagingRefusedException refused)
        {
            log.WriteLine("staging refused: " + refused.Message);
            Say(SetupPlan.UnsafeInstallPathText(Root));
            return SetupPlan.ExitUnsafeInstallPath;
        }
        using (staging)
        {
            FileStream? pawnIoHold;
            try
            {
                Write("  ▸ ", ConsoleColor.Magenta);
                Console.Write("Распаковываю и проверяю файлы… ");
                pawnIoHold = staging.Extract(payload, manifest, manifest.Any(e => e.Path == SetupPlan.PawnIoSetup) ? SetupPlan.PawnIoSetup : null);
                Write("готово", ConsoleColor.Green);
                Console.WriteLine();
            }
            catch (PayloadRejectedException rejected)
            {
                Console.WriteLine();
                log.WriteLine("payload rejected: " + rejected.Message);
                Say(SetupPlan.ManifestBrokenText);
                return 3;
            }
            using (pawnIoHold)
            {
                return InstallStaged(payload, newVersion, staging, pawnIoHold);
            }
        }
    }

    private static IReadOnlyList<ManifestEntry>? ReadManifest(ZipArchive payload)
    {
        if (payload.GetEntry(PayloadManifest.FileName) is not { } entry)
        {
            return null;
        }
        using var stream = entry.Open();
        return PayloadManifest.Read(stream); // never more than 4 MB + 1 byte, whatever the entry header says
    }

    private int InstallStaged(ZipArchive payload, string newVersion, StagingDirectory staging, FileStream? pawnIoHold)
    {
        var agentExe = SetupPlan.AgentExe(Root);
        var oldVersion = File.Exists(agentExe) ? FileVersionInfo.GetVersionInfo(agentExe).ProductVersion?.Split('+')[0] : null;
        Say(oldVersion is null ? $"Устанавливаю версию {newVersion}." : $"Обновляю {oldVersion} → {newVersion}. Сопряжённые устройства, автоматизации и настройки сохраняются.");

        // T5 section 3, stages U2–U6: atomic replacement with a journal and rollback at every stage.
        var transaction = new InstallTransaction(new WindowsInstallFileSystem(), this, Root, log, title =>
        {
            Write("  ▸ ", ConsoleColor.Magenta);
            Console.WriteLine($"{title}…");
            log.WriteLine("== " + title);
        });
        var components = SetupPlan.Parts.Where(p => Directory.Exists(Path.Combine(staging.Path, p))).ToList();
        TransactionOutcome outcome;
        try
        {
            outcome = transaction.Run(staging.Path, components, () =>
            {
                if (File.Exists(SetupPlan.OpenRgbExe(Root)))
                {
                    ImportOpenRgbSettings(); // R11-1 recheck failure → rollback, exit code 6
                }
                ((IInstallServices)this).Register();
            });
        }
        catch (UnsafeInstallObjectException unsafeObject)
        {
            // Only the first journal write is outside the stages; nothing was changed yet.
            log.WriteLine("install folder object refused: " + unsafeObject.Message);
            Say(SetupPlan.UnsafeInstallPathText(Root));
            return SetupPlan.ExitUnsafeInstallPath;
        }
        switch (outcome.Result)
        {
            case TransactionResult.RecoveryFailed:
                staging.Keep();
                log.WriteLine($"{outcome.Stage}: {outcome.Error?.Message}");
                Say(SetupPlan.RecoveryFailedText);
                return SetupPlan.ExitRecoveryIncomplete;
            case TransactionResult.RolledBack:
                log.WriteLine($"{outcome.Stage}: {outcome.Error}");
                Console.WriteLine();
                if (outcome.Error is DataDirectoryRecheckException refused)
                {
                    Say(refused.Message);
                }
                Say(SetupPlan.RolledBackText(oldVersion is not null, outcome.Error?.Message, outcome.OldHealthProblem));
                return outcome.Error is DataDirectoryRecheckException recheck ? recheck.ExitCode : 5;
        }

        // U7: PawnIO after the main installation; a failure there does not roll anything back.
        if (pawnIoHold is not null)
        {
            Step("Драйвер PawnIO (температура процессора, подсветка платы и памяти)", () => InstallPawnIo(pawnIoHold, staging.LocalPath(SetupPlan.PawnIoSetup)));
        }
        pawnIoHold?.Dispose();
        var left = transaction.Finish(staging.Path);
        log.WriteLine(left.Count == 0
            ? "Journal: committed; staging, old folders and journal removed"
            : "Journal: committed; left for the next run to remove: " + string.Join(", ", left));

        if (!options.NoTray)
        {
            Step("Запускаю Nexus PC в трее и включаю автозапуск", () =>
            {
                using (var run = Registry.CurrentUser.CreateSubKey(SetupPlan.RunKey))
                {
                    run.SetValue(SetupPlan.RunValue, $"\"{SetupPlan.TrayExe(Root)}\"");
                }
                StartTray();
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
    private void InstallPawnIo(FileStream held, string stagedSetup)
    {
        using (var key = Registry.LocalMachine.OpenSubKey(SetupPlan.PawnIoUninstallKey))
        {
            if (key is not null)
            {
                Write("уже установлен — ", ConsoleColor.DarkGray);
                return;
            }
        }
        // T5 section 4: the file written to staging and held with FileShare.Read since then; checked through that handle.
        PawnIoRun run;
        try
        {
            run = PawnIoRunner.Run(held, stagedSetup, PawnIoPin.FromAssembly(), SetupPlan.InstallPawnIo(stagedSetup).Args, log, TimeSpan.FromMinutes(10));
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            log.WriteLine("PawnIO not started: " + ex.Message); // stage U7: not fatal, as before
            run = new PawnIoRun(false, null, false, ex.Message);
        }
        if (!run.Started || run.Exit is null)
        {
            // Not fatal (stage U7): everything else works; CPU temperature and some lighting show "unavailable".
            Write(SetupPlan.PawnIoNotVerifiedText, ConsoleColor.Yellow);
            return;
        }
        if (run.RevocationUnknown)
        {
            Write(SetupPlan.PawnIoRevocationUnknownText, ConsoleColor.Yellow);
        }
        if (run.Exit != 0)
        {
            Write($"не установился (код {run.Exit}); температура процессора и подсветка платы будут недоступны — ", ConsoleColor.Yellow);
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
        RecheckAfterImport(DataDirectory, DataRepairEnvironment.ReadPrivileges);
        if (files.Count > 0)
        {
            Write("перенесены из вашего OpenRGB — ", ConsoleColor.DarkGray);
        }
    }

    /// <summary>
    /// The check again after the OpenRGB import: any refusal of <see cref="DataDirectoryGate(string, TextWriter, IReadOnlyList{string})"/>
    /// (including missing privileges) becomes <see cref="DataDirectoryRecheckException"/> with the gate's recheck text
    /// (service stopped, program files updated: no "nothing changed") and its exit code. Tests only pass other <paramref name="privileges"/>.
    /// </summary>
    internal static void RecheckAfterImport(string path, IReadOnlyList<string> privileges)
    {
        using var check = new StringWriter();
        if (DataDirectoryGate(path, check, privileges, recheck: true) is { } code)
        {
            throw new DataDirectoryRecheckException(code, check.ToString().TrimEnd());
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
    internal static int? DataDirectoryGate(string path, TextWriter output) => DataDirectoryGate(path, output, DataRepairEnvironment.ReadPrivileges);

    /// <summary>Tests only pass other <paramref name="privileges"/> (a name Windows does not know, to see the refusal).</summary>
    internal static int? DataDirectoryGate(string path, TextWriter output, IReadOnlyList<string> privileges) => DataDirectoryGate(path, output, privileges, recheck: false);

    /// <summary><paramref name="recheck"/>: the check again after the OpenRGB import; only the "stopped" wording differs (SetupPlan.GateStoppedText).</summary>
    internal static int? DataDirectoryGate(string path, TextWriter output, IReadOnlyList<string> privileges, bool recheck)
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
        using (var enabled = DataRepairEnvironment.EnablePrivileges(privileges))
        {
            // Without them the check cannot see protected objects: refuse instead of checking partly (R10-2).
            if (SetupPlan.MissingPrivilegesText(enabled.Missing, uninstall: false, recheck) is { } refusal)
            {
                output.WriteLine(refusal);
                return SetupPlan.ExitDataDirectoryUntrusted;
            }
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
            output.WriteLine($"Восстановление папки данных {path} не завершено. {SetupPlan.GateStoppedText(recheck)}");
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
        output.WriteLine($"Папка данных Nexus PC {path} защищена не так, как нужно. {SetupPlan.GateStoppedText(recheck)}");
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
    internal static string? UnfinishedRepairBlocksUninstall(string dataDirectory) => UnfinishedRepairBlocksUninstall(dataDirectory, DataRepairEnvironment.ReadPrivileges);

    /// <summary>Tests only pass other <paramref name="privileges"/> (a name Windows does not know, to see the refusal).</summary>
    internal static string? UnfinishedRepairBlocksUninstall(string dataDirectory, IReadOnlyList<string> privileges)
    {
        if (!Directory.Exists(dataDirectory))
        {
            return null;
        }
        IReadOnlyList<UnfinishedRepair> unfinished;
        using (var enabled = DataRepairEnvironment.EnablePrivileges(privileges))
        {
            // Without them an unfinished repair could stay unseen: refuse, the caller exits with 6 (R10-2).
            if (SetupPlan.MissingPrivilegesText(enabled.Missing, uninstall: true) is { } refusal)
            {
                return refusal;
            }
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

    // ------------------------------------------------------------ IInstallServices (T5 section 3)

    void IInstallServices.StopAll()
    {
        StopTray();
        StopService();
        StopLeftoverAgents();
        StopBundledOpenRgb();
        WarnAboutStandaloneOpenRgb();
    }

    void IInstallServices.StartBack()
    {
        try
        {
            if (RunQuiet(SetupPlan.ScQuery(_system32)).Exit != 1060 && File.Exists(SetupPlan.AgentExe(Root)))
            {
                RunQuiet(SetupPlan.ScStart(_system32));
                WaitForState(4, TimeSpan.FromSeconds(30));
            }
            if (!options.NoTray && File.Exists(SetupPlan.TrayExe(Root)))
            {
                StartTray();
            }
        }
        catch (Exception e) when (e is InvalidOperationException or IOException or System.ComponentModel.Win32Exception)
        {
            log.WriteLine("Starting the installed version again: " + e.Message);
        }
    }

    void IInstallServices.Register()
    {
        var agentExe = SetupPlan.AgentExe(Root);
        var exists = RunQuiet(SetupPlan.ScQuery(_system32)).Exit != 1060; // ERROR_SERVICE_DOES_NOT_EXIST
        Must(exists ? SetupPlan.ScConfig(_system32, agentExe) : SetupPlan.ScCreate(_system32, agentExe));
        RunQuiet(SetupPlan.ScDescription(_system32));
        Must(SetupPlan.ScFailure(_system32));
        RunQuiet(SetupPlan.FirewallDelete(_system32)); // also replaces the rule made by hand earlier
        Must(SetupPlan.FirewallAdd(_system32, agentExe));
        CreateStartMenuShortcut();
    }

    /// <summary>
    /// B-4: sc query (exit 1060 = no such service), "netsh advfirewall firewall show rule name=…" (exit 0 only when a rule
    /// with that name exists) and the Start menu file. An unclear answer counts as "existed", so nothing is removed for it.
    /// </summary>
    RegistrationSnapshot IInstallServices.Registration()
    {
        var service = RunQuiet(SetupPlan.ScQuery(_system32)).Exit != 1060;
        var firewall = RunQuiet(SetupPlan.FirewallShow(_system32)).Exit != 1;
        var shortcut = File.Exists(SetupPlan.StartMenuShortcut(Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms)));
        log.WriteLine($"Registration before: service {service}, firewall rule {firewall}, Start menu entry {shortcut}");
        return new RegistrationSnapshot(service, firewall, shortcut);
    }

    void IInstallServices.Unregister(RegistrationSnapshot before)
    {
        if (!before.Service)
        {
            StopService();
            RunQuiet(SetupPlan.ScDelete(_system32));
        }
        if (!before.FirewallRule)
        {
            RunQuiet(SetupPlan.FirewallDelete(_system32));
        }
        var shortcut = SetupPlan.StartMenuShortcut(Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms));
        if (!before.Shortcut && File.Exists(shortcut))
        {
            File.Delete(shortcut);
        }
        log.WriteLine($"First installation rolled back; removed only what it added (service {!before.Service}, firewall rule {!before.FirewallRule}, Start menu entry {!before.Shortcut}); data kept");
    }

    void IInstallServices.Start()
    {
        Must(SetupPlan.ScStart(_system32));
        WaitForState(4, TimeSpan.FromSeconds(30));
    }

    void IInstallServices.Stop()
    {
        StopService();
        StopLeftoverAgents();
        StopBundledOpenRgb();
    }

    /// <summary>Running, port 47800 listening, and "Nexus.Agent.exe status" answers over the admin channel; up to 60 s.</summary>
    string? IInstallServices.HealthProblem()
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(60);
        string problem;
        do
        {
            var query = RunQuiet(SetupPlan.ScQuery(_system32));
            if (SetupPlan.ServiceState(query.Exit, query.Output) != 4)
            {
                problem = "the service is not running";
            }
            else if (!System.Net.NetworkInformation.IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners().Any(e => e.Port == SetupPlan.Port))
            {
                problem = $"port {SetupPlan.Port} is not listening";
            }
            else if (RunQuiet(new Command(SetupPlan.AgentExe(Root), ["status"])).Exit != 0)
            {
                problem = "the admin channel does not answer status";
            }
            else
            {
                log.WriteLine("Health: service running, port listening, status answered");
                return null;
            }
            Thread.Sleep(1000);
        }
        while (DateTime.UtcNow < deadline);
        log.WriteLine("Health: " + problem);
        return problem;
    }

    /// <summary>Through Explorer, so the tray runs with normal rights, not as administrator.</summary>
    private void StartTray()
    {
        using var _ = Process.Start(new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"), $"\"{SetupPlan.TrayExe(Root)}\""));
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
