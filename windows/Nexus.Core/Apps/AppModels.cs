using System.Text.RegularExpressions;
using Nexus.Core.Protocol;

namespace Nexus.Core.Apps;

public enum AppLaunchKind
{
    /// <summary>Executable path, fixed on the PC.</summary>
    Executable,
    /// <summary>Packaged (Store/MSIX) app by AppUserModelID.</summary>
    PackagedApp,
    /// <summary>A .lnk or .url from the Start menu (including Steam game shortcuts), opened by the shell.</summary>
    Shortcut,
}

/// <summary>
/// An app the PC owner allowed. Target and arguments are set on the PC only; clients refer
/// to apps solely by <see cref="Id"/> (SECURITY.md §3.5).
/// </summary>
/// <param name="ProcessPath">Executable used to find the running app when closing it (null: closing unsupported).</param>
public sealed record AppEntry(string Id, string DisplayName, AppLaunchKind Kind, string Target, bool CloseAllowed, string? ProcessPath = null);

/// <summary>
/// AppUserModelID of a packaged (Store/MSIX) app: <c>PackageFamilyName!AppId</c>, e.g.
/// <c>Microsoft.WindowsCalculator_8wekyb3d8bbwe!App</c>. It is passed to explorer.exe, so only this
/// exact shape is accepted: no spaces, quotes, slashes or other shell syntax.
/// </summary>
public static partial class PackagedAppIds
{
    public const int MaxLength = 200;

    public static bool IsValid(string? aumid) =>
        aumid is { Length: > 0 and <= MaxLength } && Pattern().IsMatch(aumid);

    // Package name (letters, digits, '.', '-'), '_', 13-character publisher id, '!', application id.
    [GeneratedRegex(@"^[A-Za-z0-9.\-]{1,64}_[a-z0-9]{13}![A-Za-z][A-Za-z0-9._\-]{0,63}\z", RegexOptions.CultureInvariant)]
    private static partial Regex Pattern();
}

public sealed record ProfileDefinition(string Id, string Name, IReadOnlyList<string> AppIds);

public static class ProfileRules
{
    public const int MaxNameLength = 64;
    public const int MaxApps = 32;
    public const int MaxProfiles = 32;

    public static bool IsValidName(string? name) => Identifiers.IsValidDisplayName(name, MaxNameLength);

    /// <summary>App ids in the profile that are not in the PC's registry.</summary>
    public static IReadOnlyList<string> FindUnknownApps(ProfileDefinition profile, AppRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(registry);
        return [.. profile.AppIds.Where(id => !registry.Contains(id))];
    }
}

/// <summary>In-memory registry of allowed apps. Persistence is added with Nexus.Agent (P1b).</summary>
public sealed class AppRegistry
{
    private readonly Dictionary<string, AppEntry> _apps = new(StringComparer.Ordinal);

    public IReadOnlyCollection<AppEntry> All => _apps.Values;

    public bool Contains(string id) => _apps.ContainsKey(id);

    public bool TryGet(string id, out AppEntry entry) => _apps.TryGetValue(id, out entry!);

    public void Add(AppEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (!Identifiers.IsSlug(entry.Id))
        {
            throw new ArgumentException($"App id '{entry.Id}' is not a valid slug.", nameof(entry));
        }
        if (string.IsNullOrWhiteSpace(entry.Target))
        {
            throw new ArgumentException("App target must not be empty.", nameof(entry));
        }
        if (!_apps.TryAdd(entry.Id, entry))
        {
            throw new InvalidOperationException($"App '{entry.Id}' is already registered.");
        }
    }

    public bool Remove(string id) => _apps.Remove(id);
}
