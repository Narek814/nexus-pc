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

    public static string AccountMenu => Get(nameof(AccountMenu));
    public static string AccountTitle => Get(nameof(AccountTitle));
    public static string AccountHeading => Get(nameof(AccountHeading));
    public static string AccountChecking => Get(nameof(AccountChecking));
    public static string AccountSignedOut => Get(nameof(AccountSignedOut));
    public static string AccountSignIn => Get(nameof(AccountSignIn));
    public static string AccountStarting => Get(nameof(AccountStarting));
    public static string AccountCodeHint => Get(nameof(AccountCodeHint));
    public static string AccountExpiresIn => Get(nameof(AccountExpiresIn));
    public static string AccountCancel => Get(nameof(AccountCancel));
    public static string AccountClose => Get(nameof(AccountClose));
    public static string AccountLinked => Get(nameof(AccountLinked));
    public static string AccountPlan => Get(nameof(AccountPlan));
    public static string PlanFree => Get(nameof(PlanFree));
    public static string PlanPro => Get(nameof(PlanPro));
    public static string AccountUnlink => Get(nameof(AccountUnlink));
    public static string AccountUnlinkConfirm => Get(nameof(AccountUnlinkConfirm));
    public static string AccountAdminNote => Get(nameof(AccountAdminNote));
    public static string NoticeResuming => Get(nameof(NoticeResuming));
    public static string NoticeSignedIn => Get(nameof(NoticeSignedIn));
    public static string NoticeRetry => Get(nameof(NoticeRetry));

    public static string Problem(Nexus.Core.Account.AccountProblem problem) => problem switch
    {
        Nexus.Core.Account.AccountProblem.None => string.Empty,
        Nexus.Core.Account.AccountProblem.KeyUnavailable => KeyUnavailable,
        _ => Get("Problem" + problem),
    };

    public static string Plan(string? plan) => plan switch
    {
        null or "free" => PlanFree,
        "pro" => PlanPro,
        _ => plan,
    };

    public static string Notice(Nexus.Core.Account.LoginNotice notice) => notice switch
    {
        Nexus.Core.Account.LoginNotice.Resuming => NoticeResuming,
        Nexus.Core.Account.LoginNotice.SignedIn => NoticeSignedIn,
        Nexus.Core.Account.LoginNotice.RetryNeeded => NoticeRetry,
        _ => string.Empty,
    };

    private static string Get(string name) => Resources.GetString(name, Language) ?? name;

    [LibraryImport("kernel32.dll")]
    private static partial ushort GetUserDefaultUILanguage();
}
