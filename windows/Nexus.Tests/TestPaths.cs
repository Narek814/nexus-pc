namespace Nexus.Tests;

/// <summary>
/// Paths for the in-memory data folder tests. They are written Unix-style in the tests and turned into the
/// platform's form here, because production builds paths with <see cref="Path.Join(string, string)"/>
/// (a backslash on Windows): on Windows "/pd/NexusPC" becomes "C:\pd\NexusPC".
/// </summary>
internal static class TestPaths
{
    /// <summary>The platform separator as a string, for "Root + Sep + name".</summary>
    public static readonly string Sep = Path.DirectorySeparatorChar.ToString();

    /// <summary>Parent of the data folder.</summary>
    public static readonly string Pd = Abs("/pd");

    /// <summary>The data folder.</summary>
    public static readonly string Root = Abs("/pd/NexusPC");

    /// <summary>An absolute test path: a drive on Windows, platform separators.</summary>
    public static string Abs(string unixPath) =>
        (OperatingSystem.IsWindows() && unixPath.StartsWith('/') ? "C:" : "") + Rel(unixPath);

    /// <summary>A relative path or a path fragment ("/config.json", "data/x"): platform separators only.</summary>
    public static string Rel(string unixPath) => unixPath.Replace('/', Path.DirectorySeparatorChar);

    /// <summary>The folder part of a path, cut at the last platform separator without any normalization.</summary>
    public static string? Parent(string path)
    {
        var at = path.LastIndexOf(Path.DirectorySeparatorChar);
        return at < 0 ? null : at == 0 ? Sep : path[..at];
    }

    /// <summary>The last name of a path, cut at the last platform separator.</summary>
    public static string Name(string path) => path[(path.LastIndexOf(Path.DirectorySeparatorChar) + 1)..];
}
