using System.Text.RegularExpressions;
using System.Xml.Linq;
using Nexus.Core.Account;

namespace Nexus.Tests;

/// <summary>
/// W-TRAY: tray strings in Strings.resx (English) and Strings.ru.resx, read from the sources (Nexus.Tray targets
/// net10.0-windows and is not referenced by the tests), the Win32 classification of an elevated start and the
/// elevated exe check moved to Nexus.Core.
/// </summary>
public sealed partial class TrayTextTests
{
    private static string TrayFolder()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "windows", "Nexus.Tray");
            if (File.Exists(Path.Combine(candidate, "Nexus.Tray.csproj")))
            {
                return candidate;
            }
        }
        throw new DirectoryNotFoundException("windows/Nexus.Tray not found above the test output.");
    }

    private static Dictionary<string, string> Read(string name) =>
        XDocument.Load(Path.Combine(TrayFolder(), name)).Root!.Elements("data")
            .ToDictionary(d => d.Attribute("name")!.Value, d => d.Element("value")!.Value, StringComparer.Ordinal);

    private static List<(string File, int Line, string Text)> SourceLines()
    {
        var files = Directory.EnumerateFiles(TrayFolder(), "*.cs", SearchOption.TopDirectoryOnly).ToList();
        Assert.NotEmpty(files);
        return [.. files.SelectMany(f => File.ReadAllLines(f).Select((text, i) => (Path.GetFileName(f), i + 1, text)))];
    }

    /// <summary>Keys reached through a variable or a condition, not as a literal in TrayText.Get/Format.</summary>
    private static readonly string[] IndirectKeys =
    [
        "TrayStatusServiceDown", "TrayStatusServiceUntrusted", "TrayTipReady", "TrayTipServiceDown", "PairDone", "PairDeclined",
        "PermPower", "PermApps", "PermMonitoring", "PermRgb", "PermInput", "PermScreen", "PermFiles", "PermClipboard", "PermAutomation",
        "ProblemElevationFailed",
    ];

    [Fact]
    public void English_and_russian_have_the_same_keys()
    {
        var en = Read("Strings.resx");
        var ru = Read("Strings.ru.resx");
        Assert.Equal(en.Keys.Order(StringComparer.Ordinal), ru.Keys.Order(StringComparer.Ordinal));
        Assert.All(en.Concat(ru), pair => Assert.False(string.IsNullOrWhiteSpace(pair.Value), pair.Key));
    }

    [Fact]
    public void Placeholders_match_between_languages()
    {
        var en = Read("Strings.resx");
        var ru = Read("Strings.ru.resx");
        foreach (var (key, value) in en)
        {
            Assert.True(Placeholders(value).SequenceEqual(Placeholders(ru[key])), key);
        }
    }

    [Fact]
    public void Every_key_the_tray_uses_exists()
    {
        var en = Read("Strings.resx");
        var used = SourceLines().SelectMany(l => KeyUse().Matches(l.Text).Select(m => m.Groups[1].Value)).Concat(IndirectKeys).Distinct().ToList();
        Assert.True(used.Count > 50, "too few keys found: " + used.Count);
        Assert.All(used, key => Assert.True(en.ContainsKey(key), "missing key " + key));
    }

    [Fact]
    public void No_russian_text_outside_comments_in_tray_code()
    {
        var offending = SourceLines()
            .Where(l => !l.Text.TrimStart().StartsWith("//", StringComparison.Ordinal) && Cyrillic().IsMatch(CodePart(l.Text)))
            .Select(l => $"{l.File}:{l.Line}")
            .ToList();
        Assert.Empty(offending);
    }

    [Fact]
    public void Tray_log_never_gets_an_exception_message()
    {
        var offending = SourceLines()
            .Where(l => l.Text.Contains("TrayLog.Write", StringComparison.Ordinal) && l.Text.Contains(".Message", StringComparison.Ordinal))
            .Select(l => $"{l.File}:{l.Line}")
            .ToList();
        Assert.Empty(offending);
    }

    [Fact]
    public void Tray_tips_fit_the_notify_icon_limit()
    {
        foreach (var file in new[] { "Strings.resx", "Strings.ru.resx" })
        {
            var strings = Read(file);
            Assert.All(strings.Where(p => p.Key.StartsWith("TrayTip", StringComparison.Ordinal) || p.Key == "KeyUnavailableTip"),
                p => Assert.True(p.Value.Replace("{0}", "", StringComparison.Ordinal).Length < 63, file + " " + p.Key));
        }
    }

    [Fact]
    public void User_texts_have_no_internal_terms()
    {
        string[] banned = ["PowerShell", "служб", "канал", "pipe", "DevClient", "docs/", "--"];
        foreach (var file in new[] { "Strings.resx", "Strings.ru.resx" })
        {
            Assert.All(Read(file), p => Assert.DoesNotContain(banned, b => p.Value.Contains(b, StringComparison.OrdinalIgnoreCase)));
        }
    }

    [Fact]
    public void Check_for_updates_only_opens_a_fixed_https_page()
    {
        var settings = File.ReadAllText(Path.Combine(TrayFolder(), "SettingsForm.cs"));
        Assert.Matches("internal const string DownloadUrl = \"https://[^\"]+\";", settings);
        Assert.Contains("OpenInShell(DownloadUrl)", settings, StringComparison.Ordinal);
        string[] network = ["HttpClient", "WebRequest", "WebClient", "Socket", "System.Net"];
        Assert.DoesNotContain(network, n => settings.Contains(n, StringComparison.Ordinal));
    }

    [Fact]
    public void Tray_double_click_opens_devices()
    {
        var tray = File.ReadAllText(Path.Combine(TrayFolder(), "TrayContext.cs"));
        Assert.Contains("_icon.DoubleClick += (_, _) => ShowDevices(agentPath);", tray, StringComparison.Ordinal);
    }

    /// <summary>The one classification DevicesForm, RunAdminCommand and the account step share.</summary>
    [Theory]
    [InlineData(1223, true)] // ERROR_CANCELLED: "Cancelled"
    [InlineData(0, false)]
    [InlineData(2, false)] // file not found
    [InlineData(5, false)] // access denied
    [InlineData(193, false)] // not a valid program
    [InlineData(740, false)] // elevation required
    [InlineData(1155, false)] // no program for the file
    [InlineData(-1, false)]
    public void Win32_error_of_an_elevated_start_is_cancelled_only_for_1223(int nativeError, bool cancelled)
    {
        Assert.Equal(cancelled, ElevatedAccountCommand.IsUacDeclined(nativeError));
    }

    [Theory]
    [InlineData(null, false, "elevation")]
    [InlineData("", true, "elevation")]
    [InlineData("   ", true, "elevation")]
    [InlineData(@"C:\Program Files\Nexus PC\Tray\Nexus.Tray.exe", false, "elevation")]
    [InlineData(@"C:\Program Files\Nexus PC\Tray\Nexus.Tray.exe", true, null)]
    public void Elevated_exe_path_is_checked_before_the_start(string? path, bool exists, string? expected)
    {
        var asked = new List<string>();
        Assert.Equal(expected, ElevatedAccountCommand.CheckElevatedPath(path, p => { asked.Add(p); return exists; }));
        Assert.True(asked.Count <= 1);
        if (expected is not null)
        {
            Assert.Equal("elevation", ElevatedAccountCommand.ErrorOf(ElevatedAccountCommand.ExitCodeOfError(expected)));
        }
    }

    private static List<string> Placeholders(string value) =>
        [.. Placeholder().Matches(value).Select(m => m.Value).Distinct().Order(StringComparer.Ordinal)];

    /// <summary>The code before a trailing // comment (a string containing "//", like an https address, is kept).</summary>
    private static string CodePart(string line)
    {
        var at = line.IndexOf(" // ", StringComparison.Ordinal);
        return at < 0 ? line : line[..at];
    }

    [GeneratedRegex(@"TrayText\.(?:Get|Format)\(""([A-Za-z0-9]+)""")]
    private static partial Regex KeyUse();

    [GeneratedRegex(@"\p{IsCyrillic}")]
    private static partial Regex Cyrillic();

    [GeneratedRegex(@"\{\d+\}")]
    private static partial Regex Placeholder();
}
