using System.Text.RegularExpressions;

namespace Nexus.Core.Protocol;

public static partial class Identifiers
{
    /// <summary>
    /// App/profile ids: lowercase slug, never a path. Matches `$defs.slug` in the schema.
    /// \z instead of $: .NET's $ would accept a trailing newline.
    /// </summary>
    public static bool IsSlug(string? value) => value is not null && Slug().IsMatch(value);

    /// <summary>1..max Unicode code points (the JSON Schema unit), not blank, no control characters.</summary>
    public static bool IsValidDisplayName(string? name, int maxLength = 64)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }
        var count = 0;
        foreach (var rune in name.EnumerateRunes())
        {
            if (rune.Value <= 0x1F || rune.Value == 0x7F)
            {
                return false;
            }
            count++;
        }
        return count <= maxLength;
    }

    /// <summary>Makes a slug from a display name (ASCII letters/digits kept, Cyrillic transliterated), unique among <paramref name="taken"/>.</summary>
    public static string Slugify(string name, IEnumerable<string> taken)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(taken);
        var used = taken.ToHashSet(StringComparer.Ordinal);
        var builder = new System.Text.StringBuilder();
        foreach (var c in name.ToLowerInvariant())
        {
            if (c is (>= 'a' and <= 'z') or (>= '0' and <= '9'))
            {
                builder.Append(c);
            }
            else if (Cyrillic.TryGetValue(c, out var latin))
            {
                builder.Append(latin);
            }
            else if (builder.Length > 0 && builder[^1] != '-')
            {
                builder.Append('-');
            }
        }
        var slug = builder.ToString().Trim('-');
        if (slug.Length == 0)
        {
            slug = "app";
        }
        if (slug.Length > 56)
        {
            slug = slug[..56].TrimEnd('-');
        }
        var candidate = slug;
        for (var i = 2; used.Contains(candidate); i++)
        {
            candidate = $"{slug}-{i}";
        }
        return candidate;
    }

    private static readonly Dictionary<char, string> Cyrillic = new()
    {
        ['а'] = "a", ['б'] = "b", ['в'] = "v", ['г'] = "g", ['д'] = "d", ['е'] = "e", ['ё'] = "e", ['ж'] = "zh",
        ['з'] = "z", ['и'] = "i", ['й'] = "y", ['к'] = "k", ['л'] = "l", ['м'] = "m", ['н'] = "n", ['о'] = "o",
        ['п'] = "p", ['р'] = "r", ['с'] = "s", ['т'] = "t", ['у'] = "u", ['ф'] = "f", ['х'] = "h", ['ц'] = "ts",
        ['ч'] = "ch", ['ш'] = "sh", ['щ'] = "sch", ['ъ'] = "", ['ы'] = "y", ['ь'] = "", ['э'] = "e", ['ю'] = "yu", ['я'] = "ya",
    };

    [GeneratedRegex(@"^[a-z0-9][a-z0-9-]{0,63}\z", RegexOptions.CultureInvariant)]
    private static partial Regex Slug();
}
