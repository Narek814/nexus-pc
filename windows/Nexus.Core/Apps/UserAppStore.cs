using System.Text.Json;
using System.Text.Json.Serialization;
using Nexus.Core.Protocol;

namespace Nexus.Core.Apps;

/// <summary>
/// The person's allowed apps and profiles (apps.json, profiles.json), kept by Nexus.Tray in the
/// user's own profile folder. Apps can only be added on the PC; phones refer to them by id.
/// Profiles may be edited from a phone but can only contain apps from this list.
/// </summary>
public sealed class UserAppStore
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
        AllowDuplicateProperties = false,
    };

    private readonly string _appsPath;
    private readonly string _profilesPath;
    private readonly Lock _lock = new();
    private readonly AppRegistry _apps = new();
    private List<ProfileDefinition> _profiles = [];

    public UserAppStore(string directory)
    {
        ArgumentNullException.ThrowIfNull(directory);
        Directory.CreateDirectory(directory);
        _appsPath = Path.Combine(directory, "apps.json");
        _profilesPath = Path.Combine(directory, "profiles.json");

        if (File.Exists(_appsPath))
        {
            foreach (var app in JsonSerializer.Deserialize<List<AppEntry>>(File.ReadAllText(_appsPath), Json) ?? [])
            {
                _apps.Add(app); // validates ids and targets
            }
        }
        if (File.Exists(_profilesPath))
        {
            _profiles = JsonSerializer.Deserialize<List<ProfileDefinition>>(File.ReadAllText(_profilesPath), Json) ?? [];
            // Drop references to apps that were removed while the file was edited elsewhere.
            _profiles = [.. _profiles.Where(p => Identifiers.IsSlug(p.Id) && ProfileRules.IsValidName(p.Name))
                .Select(p => p with { AppIds = [.. p.AppIds.Where(_apps.Contains).Distinct(StringComparer.Ordinal)] })];
        }
    }

    public event EventHandler? Changed;

    public IReadOnlyList<AppEntry> Apps
    {
        get
        {
            lock (_lock)
            {
                return [.. _apps.All.OrderBy(a => a.DisplayName, StringComparer.CurrentCultureIgnoreCase)];
            }
        }
    }

    public IReadOnlyList<ProfileDefinition> Profiles
    {
        get
        {
            lock (_lock)
            {
                return [.. _profiles];
            }
        }
    }

    public AppEntry? FindApp(string id)
    {
        lock (_lock)
        {
            return _apps.TryGet(id, out var app) ? app : null;
        }
    }

    public ProfileDefinition? FindProfile(string id)
    {
        lock (_lock)
        {
            return _profiles.Find(p => p.Id == id);
        }
    }

    /// <summary>Adds an app chosen on the PC. The id is derived from the name.</summary>
    public AppEntry AddApp(string displayName, AppLaunchKind kind, string target, bool closeAllowed, string? processPath)
    {
        if (!Identifiers.IsValidDisplayName(displayName))
        {
            throw new ArgumentException("App name must be 1..64 visible characters.", nameof(displayName));
        }
        lock (_lock)
        {
            if (_apps.All.Count >= 128)
            {
                throw new InvalidOperationException("At most 128 apps can be added.");
            }
            var entry = new AppEntry(Identifiers.Slugify(displayName, _apps.All.Select(a => a.Id)), displayName, kind, target, closeAllowed, processPath);
            _apps.Add(entry);
            SaveLocked();
            return entry;
        }
    }

    public bool RemoveApp(string id)
    {
        lock (_lock)
        {
            if (!_apps.Remove(id))
            {
                return false;
            }
            _profiles = [.. _profiles.Select(p => p with { AppIds = [.. p.AppIds.Where(a => a != id)] })];
            SaveLocked();
            return true;
        }
    }

    /// <summary>Creates or replaces a profile. Returns app ids that are not in the list (then nothing is saved).</summary>
    public IReadOnlyList<string> UpsertProfile(ProfileDefinition profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        lock (_lock)
        {
            var unknown = ProfileRules.FindUnknownApps(profile, _apps);
            if (unknown.Count > 0)
            {
                return unknown;
            }
            var index = _profiles.FindIndex(p => p.Id == profile.Id);
            if (index < 0 && _profiles.Count >= ProfileRules.MaxProfiles)
            {
                throw new InvalidOperationException($"At most {ProfileRules.MaxProfiles} profiles.");
            }
            var updated = _profiles.ToList();
            if (index < 0)
            {
                updated.Add(profile);
            }
            else
            {
                updated[index] = profile;
            }
            _profiles = updated;
            SaveLocked();
            return [];
        }
    }

    public bool DeleteProfile(string id)
    {
        lock (_lock)
        {
            if (_profiles.RemoveAll(p => p.Id == id) == 0)
            {
                return false;
            }
            SaveLocked();
            return true;
        }
    }

    private void SaveLocked()
    {
        Write(_appsPath, JsonSerializer.Serialize(_apps.All.ToList(), Json));
        Write(_profilesPath, JsonSerializer.Serialize(_profiles, Json));
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private static void Write(string path, string contents)
    {
        var temp = path + ".tmp";
        File.WriteAllText(temp, contents);
        File.Move(temp, path, overwrite: true);
    }
}
