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

    [GeneratedRegex(@"^[a-z0-9][a-z0-9-]{0,63}\z", RegexOptions.CultureInvariant)]
    private static partial Regex Slug();
}
