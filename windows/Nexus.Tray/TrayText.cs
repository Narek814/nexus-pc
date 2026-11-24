using System.Globalization;
using System.Resources;
using System.Runtime.InteropServices;

namespace Nexus.Tray;

/// <summary>
/// Tray strings from Strings.resx (English, neutral) and Strings.ru.resx. The language follows the Windows display
/// language (GetUserDefaultUILanguage): the process runs with invariant globalization, so CurrentUICulture would
/// always be the invariant one.
/// </summary>
internal static partial class TrayText
{
    private const int LangRussian = 0x19;
    private static readonly ResourceManager Resources = new("Nexus.Tray.Strings", typeof(TrayText).Assembly);
    private static readonly CultureInfo Language = (GetUserDefaultUILanguage() & 0x3FF) == LangRussian
        ? CultureInfo.GetCultureInfo("ru")
        : CultureInfo.InvariantCulture;

    public static string KeyUnavailable => Get(nameof(KeyUnavailable));
    public static string KeyUnavailableTip => Get(nameof(KeyUnavailableTip));
    public static string KeyRetry => Get(nameof(KeyRetry));

    private static string Get(string name) => Resources.GetString(name, Language) ?? name;

    [LibraryImport("kernel32.dll")]
    private static partial ushort GetUserDefaultUILanguage();
}
