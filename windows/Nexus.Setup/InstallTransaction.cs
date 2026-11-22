namespace Nexus.Setup;

/// <summary>An object the transaction was about to move or delete is a link or not owned by a trusted owner (B-3): refused, nothing touched.</summary>
internal sealed class UnsafeInstallObjectException(string message) : Exception(message);

/// <summary>What existed before the first installation, so its rollback removes only what this run added (B-4).</summary>
internal sealed record RegistrationSnapshot(bool Service, bool FirewallRule, bool Shortcut)
{
    /// <summary>Unknown (an older journal): treat everything as pre-existing, i.e. remove nothing.</summary>
    public static readonly RegistrationSnapshot AllExisted = new(true, true, true);
}

/// <summary>File operations of the transaction; on Windows <see cref="WindowsInstallFileSystem"/>, in tests a fake.</summary>
internal interface IInstallFileSystem
{
    bool DirectoryExists(string path);

    /// <summary>
    /// Opens the object by handle without following links (B-3). Throws <see cref="UnsafeInstallObjectException"/> when it is
    /// a reparse point or its owner is not SYSTEM, Administrators or TrustedInstaller. Missing objects pass.
    /// </summary>
    void CheckObject(string path);

    /// <summary>Atomic rename on one volume (MoveFileEx without copying, never replacing an existing target).</summary>
    void MoveDirectory(string from, string to);

    void DeleteDirectory(string path);

    IReadOnlyList<string> ListDirectories(string root);

    string? ReadJournal(string path);

    /// <summary>Temporary file → flushed → renamed over the journal (write-through). Journal and .tmp are checked first (B-3).</summary>
    void WriteJournal(string path, string contents);

    void DeleteJournal(string path);

    /// <summary>Installed-folder DACL (M2): SYSTEM and Administrators full, Users read and execute, protected; owner Administrators.</summary>
    void ProtectInstalled(string path);

    /// <summary>
    /// B-2: the staging folder a rollback moves new components back into. Created if missing (protected DACL, owner
    /// Administrators) and checked like a new staging folder; throws when it fails the check.
    /// </summary>
    void EnsureStaging(string path);
}

/// <summary>Service, tray and registration actions of the transaction; on Windows the installer itself, in tests a fake.</summary>
internal interface IInstallServices
{
    /// <summary>U2: stop the tray, the service, leftover agents and the bundled OpenRGB.</summary>
    void StopAll();

    /// <summary>Start the service and the tray of whatever is installed now; best effort, never throws.</summary>
    void StartBack();

    /// <summary>What is registered now (before U4 changes anything).</summary>
    RegistrationSnapshot Registration();

    /// <summary>Service registration, failure actions, firewall rule, Start menu entry (idempotent; same paths for every version).</summary>
    void Register();

    /// <summary>First installation rolled back: remove what did not exist before (<paramref name="before"/>). Data stays.</summary>
    void Unregister(RegistrationSnapshot before);

    /// <summary>U5: start the service; throws when it does not reach Running.</summary>
    void Start();

    void Stop();

    /// <summary>U5 health: service Running, port 47800 listening, the admin channel answers "status". Null when healthy.</summary>
    string? HealthProblem();
}

internal enum TransactionResult
{
    Committed,
    RolledBack,
    /// <summary>The rollback itself failed: nothing was deleted, the journal and every .old / staging folder are kept.</summary>
    RecoveryFailed,
}

/// <param name="Stage">U2…U6 where it failed (empty when committed).</param>
/// <param name="OldHealthProblem">After a rollback from U4/U5: what the old version's health check said (null = healthy or not checked).</param>
internal sealed record TransactionOutcome(TransactionResult Result, string Stage, Exception? Error, string? OldHealthProblem = null);

internal enum RecoveryResult
{
    /// <summary>No journal: only staging folders of earlier runs were removed (nothing had moved out of them).</summary>
    Nothing,
    /// <summary>The last run had committed: its .old folders, staging and journal were removed.</summary>
    Cleaned,
    /// <summary>The last run had not committed: the old version was put back, then leftovers removed.</summary>
    RolledBack,
    /// <summary>The rollback failed: nothing deleted.</summary>
    Failed,
    /// <summary>B-1: the journal exists but is unreadable, malformed or too large: nothing touched, the person checks it.</summary>
    JournalUnreadable,
}

/// <summary>
/// T5 section 3: atomic replacement of Agent, Tray and OpenRGB with a write-ahead journal and rollback at every stage.
/// Every action is first written to the journal, then done. Recovery reads the journal and the folders that exist; since
/// every move is an atomic rename, each component is always either old-in-place, old-moved-aside, or new-in-place, and
/// the rollback is the same function for a failure now and for an interrupted earlier run (idempotent).
/// Only our own folders inside the install root are ever moved or deleted (C, C.old-&lt;id&gt;, .staging-&lt;id&gt;); never data.
/// </summary>
internal sealed class InstallTransaction(IInstallFileSystem fs, IInstallServices services, string root, TextWriter log, Action<string>? progress = null)
{
    private string JournalPath => InstallJournal.PathIn(root);

    private void Save(InstallJournal journal)
    {
        fs.WriteJournal(JournalPath, journal.Serialize());
        log.WriteLine($"Journal: {journal.State} {journal.Step}".TrimEnd());
    }

    /// <summary>U2–U6. <paramref name="stagingPath"/> is root\.staging-&lt;id&gt;; <paramref name="configure"/> is stage U4.</summary>
    public TransactionOutcome Run(string stagingPath, IReadOnlyList<string> components, Action configure)
    {
        ArgumentNullException.ThrowIfNull(stagingPath);
        ArgumentNullException.ThrowIfNull(components);
        ArgumentNullException.ThrowIfNull(configure);
        var id = Path.GetFileName(stagingPath)[StagingDirectoryName.Prefix.Length..];
        // B-3: everything this run will move is checked before anything changes (refusal → exit code 8, nothing touched).
        foreach (var c in components)
        {
            fs.CheckObject(Path.Join(root, c));
            fs.CheckObject(Path.Join(stagingPath, c));
            fs.CheckObject(InstallJournal.OldPath(root, c, id));
        }
        var plan = components.Select(c => new JournalComponent(c, fs.DirectoryExists(Path.Join(root, c)))).ToList();
        var firstInstall = !fs.DirectoryExists(Path.Join(root, "Agent"));
        var journal = new InstallJournal(id, stagingPath, firstInstall, InstallJournal.Prepared, plan) { Before = services.Registration() };
        Save(journal);

        // U2
        progress?.Invoke("Останавливаю Nexus PC");
        try
        {
            Save(journal = journal with { State = InstallJournal.Stopping });
            services.StopAll();
        }
        catch (Exception e)
        {
            log.WriteLine("U2 failed: " + e.Message);
            services.StartBack();
            TryDeleteJournal();
            return new TransactionOutcome(TransactionResult.RolledBack, "U2", e);
        }

        // U3
        progress?.Invoke("Заменяю файлы");
        try
        {
            Save(journal = journal with { State = InstallJournal.Replacing });
            foreach (var c in plan)
            {
                var current = Path.Join(root, c.Name);
                var fresh = Path.Join(stagingPath, c.Name);
                if (c.HadOld)
                {
                    Save(journal = journal with { Step = $"move {c.Name} -> {c.Name}{InstallJournal.OldInfix}{id}" });
                    Move(current, InstallJournal.OldPath(root, c.Name, id));
                }
                fs.ProtectInstalled(fresh);
                Save(journal = journal with { Step = $"move staging {c.Name} -> {c.Name}" });
                Move(fresh, current);
            }
        }
        catch (Exception e)
        {
            return Fail(journal, "U3", e, stopNew: false);
        }

        // U4
        progress?.Invoke("Настраиваю службу, брандмауэр и меню «Пуск»");
        try
        {
            Save(journal = journal with { State = InstallJournal.Registering, Step = "" });
            configure();
        }
        catch (Exception e)
        {
            return Fail(journal, "U4", e, stopNew: false);
        }

        // U5
        progress?.Invoke("Запускаю службу и проверяю, что она отвечает");
        try
        {
            Save(journal = journal with { State = InstallJournal.Starting });
            services.Start();
            if (services.HealthProblem() is { } problem)
            {
                throw new InvalidOperationException(problem);
            }
        }
        catch (Exception e)
        {
            return Fail(journal, "U5", e, stopNew: true);
        }

        // U6
        try
        {
            Save(journal = journal with { State = InstallJournal.Committed });
        }
        catch (Exception e)
        {
            return Fail(journal, "U6", e, stopNew: true);
        }
        DeleteOlds(journal);
        return new TransactionOutcome(TransactionResult.Committed, "", null);
    }

    /// <summary>
    /// After U7 (PawnIO): removes the .old folders, the staging folder and then the journal of a committed run. Returns what
    /// is left (empty = everything removed); while anything is left the committed journal stays and the next run removes it.
    /// </summary>
    public IReadOnlyList<string> Finish(string stagingPath)
    {
        var journal = fs.ReadJournal(JournalPath) is { } text ? InstallJournal.Parse(text, root) : null;
        if (journal is not null)
        {
            DeleteOlds(journal); // again: a folder still in use at U6 may be free now
        }
        TryDelete(stagingPath);
        var left = (journal?.Components.Where(c => c.HadOld).Select(c => InstallJournal.OldPath(root, c.Name, journal.Id)) ?? [])
            .Append(stagingPath).Where(fs.DirectoryExists).ToList();
        if (left.Count == 0)
        {
            TryDeleteJournal();
            if (fs.ReadJournal(JournalPath) is not null)
            {
                left.Add(JournalPath);
            }
        }
        else
        {
            left.Add(JournalPath);
        }
        return left;
    }

    private TransactionOutcome Fail(InstallJournal journal, string stage, Exception error, bool stopNew)
    {
        log.WriteLine($"{stage} failed: {error.Message}; rolling back");
        try
        {
            if (stopNew)
            {
                services.Stop();
            }
            Rollback(journal);
            if (journal.FirstInstall)
            {
                services.Unregister(journal.Before);
            }
            else if (journal.RegistrationTouched)
            {
                services.Register();
            }
        }
        catch (Exception rollback)
        {
            log.WriteLine($"Rollback failed ({rollback.Message}): nothing deleted, journal kept for the next run");
            return new TransactionOutcome(TransactionResult.RecoveryFailed, stage, error);
        }
        TryDeleteJournal(); // the new files are back in staging; the caller removes staging
        string? oldProblem = null;
        if (!journal.FirstInstall)
        {
            services.StartBack();
            if (journal.RegistrationTouched)
            {
                oldProblem = services.HealthProblem();
                log.WriteLine("Old version health after rollback: " + (oldProblem ?? "ok"));
            }
        }
        log.WriteLine("Journal: rolled back");
        return new TransactionOutcome(TransactionResult.RolledBack, stage, error, oldProblem);
    }

    /// <summary>
    /// Puts every component back, last one first. Per component (cur = root\C, old = C.old-id, new = staging\C):
    /// old exists → (cur exists → cur back to new) and old → cur; no old and the component was new → cur back to new.
    /// Anything else (old in place, or nothing moved yet) needs nothing. Contradictory states throw: nothing is guessed.
    /// </summary>
    private void Rollback(InstallJournal journal)
    {
        // B-3: every object the rollback may move is checked before the first move, so a refusal changes nothing.
        foreach (var c in journal.Components)
        {
            fs.CheckObject(Path.Join(root, c.Name));
            fs.CheckObject(InstallJournal.OldPath(root, c.Name, journal.Id));
            fs.CheckObject(Path.Join(journal.Staging, c.Name));
        }
        RollbackChecked(journal);
    }

    private void RollbackChecked(InstallJournal journal)
    {
        foreach (var c in journal.Components.Reverse())
        {
            var current = Path.Join(root, c.Name);
            var old = InstallJournal.OldPath(root, c.Name, journal.Id);
            var fresh = Path.Join(journal.Staging, c.Name);
            if (fs.DirectoryExists(old))
            {
                if (!c.HadOld)
                {
                    throw new InvalidOperationException($"unexpected {old}");
                }
                if (fs.DirectoryExists(current))
                {
                    if (fs.DirectoryExists(fresh))
                    {
                        throw new InvalidOperationException($"{current}, {old} and {fresh} all exist");
                    }
                    fs.EnsureStaging(journal.Staging);
                    Move(current, fresh);
                }
                Move(old, current);
                log.WriteLine($"Rolled back {c.Name}");
            }
            else if (!c.HadOld && fs.DirectoryExists(current))
            {
                if (fs.DirectoryExists(fresh))
                {
                    throw new InvalidOperationException($"{current} and {fresh} both exist");
                }
                fs.EnsureStaging(journal.Staging);
                Move(current, fresh);
                log.WriteLine($"Removed new {c.Name} (it did not exist before)");
            }
            else if (c.HadOld && !fs.DirectoryExists(current))
            {
                throw new InvalidOperationException($"{current} and {old} are both missing");
            }
        }
    }

    /// <summary>Start of every installation: finishes or undoes an interrupted earlier run, from its journal.</summary>
    public RecoveryResult Recover()
    {
        string? text;
        try
        {
            text = fs.ReadJournal(JournalPath);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or UnsafeInstallObjectException)
        {
            log.WriteLine("Install journal cannot be read (" + e.Message + "): nothing touched");
            return RecoveryResult.JournalUnreadable;
        }
        if (text is null)
        {
            foreach (var dir in OurDirectories(StagingDirectoryName.Prefix))
            {
                TryDelete(dir); // a journal is written before anything moves out of staging: nothing of ours is in it
            }
            foreach (var dir in OurDirectories(InstallJournal.OldInfix))
            {
                log.WriteLine("Kept (no journal says whose it is): " + dir);
            }
            return RecoveryResult.Nothing;
        }
        var journal = InstallJournal.Parse(text, root);
        if (journal is null)
        {
            log.WriteLine("Install journal unreadable: nothing touched");
            return RecoveryResult.JournalUnreadable;
        }
        log.WriteLine($"Install journal of an earlier run found: {journal.State} {journal.Step}");
        if (journal.State == InstallJournal.Committed)
        {
            DeleteOlds(journal);
            TryDelete(journal.Staging);
            TryDeleteJournal();
            return RecoveryResult.Cleaned;
        }
        try
        {
            services.StopAll();
        }
        catch (Exception e)
        {
            log.WriteLine("Stopping before recovery: " + e.Message);
        }
        try
        {
            Rollback(journal);
            if (journal.FirstInstall)
            {
                services.Unregister(journal.Before);
            }
            else if (journal.RegistrationTouched)
            {
                services.Register();
            }
        }
        catch (Exception e)
        {
            log.WriteLine($"Recovery failed ({e.Message}): nothing deleted");
            return RecoveryResult.Failed;
        }
        TryDelete(journal.Staging);
        TryDeleteJournal();
        foreach (var dir in OurDirectories(StagingDirectoryName.Prefix))
        {
            TryDelete(dir);
        }
        if (!journal.FirstInstall)
        {
            services.StartBack();
        }
        log.WriteLine("Journal: earlier run rolled back");
        return RecoveryResult.RolledBack;
    }

    private IEnumerable<string> OurDirectories(string marker) =>
        fs.DirectoryExists(root)
            ? fs.ListDirectories(root).Where(d => marker == StagingDirectoryName.Prefix
                ? Path.GetFileName(d).StartsWith(marker, StringComparison.Ordinal) && InstallJournal.IsId(Path.GetFileName(d)[marker.Length..])
                : SetupPlan.Parts.Any(p => Path.GetFileName(d).StartsWith(p + marker, StringComparison.Ordinal)))
            : [];


    /// <summary>B-3: the source is opened by handle first; a link or an untrusted owner refuses the move.</summary>
    private void Move(string from, string to)
    {
        fs.CheckObject(from);
        fs.MoveDirectory(from, to);
    }
    private void DeleteOlds(InstallJournal journal)
    {
        foreach (var c in journal.Components.Where(c => c.HadOld))
        {
            TryDelete(InstallJournal.OldPath(root, c.Name, journal.Id));
        }
    }

    private void TryDelete(string path)
    {
        try
        {
            if (fs.DirectoryExists(path))
            {
                fs.CheckObject(path);
                fs.DeleteDirectory(path);
                log.WriteLine("Removed " + path);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            log.WriteLine($"Not removed now ({e.Message}), the next run removes it: {path}");
        }
        catch (UnsafeInstallObjectException e)
        {
            log.WriteLine($"Refused to remove (not touched): {e.Message}");
        }
    }

    private void TryDeleteJournal()
    {
        try
        {
            fs.DeleteJournal(JournalPath);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            log.WriteLine("Journal not removed now: " + e.Message);
        }
    }
}
