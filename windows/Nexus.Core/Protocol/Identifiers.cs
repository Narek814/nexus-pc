using System.Text.RegularExpressions;

namespace Nexus.Core.Protocol;

public static partial class Identifiers
{
    /// <summary>
    /// App/profile ids: lowercase slug, never a path. Matches `$defs.slug` in the schema.
    /// \z instead of $: .NET's $ would accept a trailing newline.
    /// </summary>
    public static bool IsSlug(string? value) => value is not null && Slug().IsMatch(value);

    [GeneratedRegex(@"^[a-z0-9][a-z0-9-]{0,63}\z", RegexOptions.CultureInvariant)]
    private static partial Regex Slug();
}
