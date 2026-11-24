using System.Reflection;

namespace Nexus.Agent.Account;

/// <summary>
/// Where the account server is (E4-4): base URL of the Supabase project and its anon key. Both come from the build
/// (MSBuild properties NexusAccountServerUrl and NexusAccountAnonKey, written into assembly metadata). Until the
/// domain exists they are empty and the account client is not created at all (same idea as ENT-2 variant A).
/// </summary>
internal sealed record AccountServerConfig(Uri BaseUrl, string AnonKey)
{
    public const string UrlMetadata = "NexusAccountServerUrl";
    public const string AnonKeyMetadata = "NexusAccountAnonKey";

    /// <summary>The values built into this agent, or null when the build has none (functionality off).</summary>
    public static AccountServerConfig? FromBuild()
    {
        var metadata = typeof(AccountServerConfig).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .ToDictionary(a => a.Key, a => a.Value, StringComparer.Ordinal);
        return TryCreate(metadata.GetValueOrDefault(UrlMetadata), metadata.GetValueOrDefault(AnonKeyMetadata));
    }

    /// <summary>Only an absolute https URL without user info, query or fragment, and a non-empty printable key.</summary>
    public static AccountServerConfig? TryCreate(string? url, string? anonKey)
    {
        if (string.IsNullOrWhiteSpace(url) || string.IsNullOrWhiteSpace(anonKey)
            || anonKey.Length > 4096 || anonKey.Any(c => c <= ' ' || c > '~'))
        {
            return null;
        }
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps
            || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0)
        {
            return null;
        }
        var text = uri.GetLeftPart(UriPartial.Path);
        return new AccountServerConfig(new Uri(text.EndsWith('/') ? text : text + "/"), anonKey);
    }

    /// <summary>Test servers only (the fake HTTP server in Nexus.Tests): plain http is allowed here, never from the build.</summary>
    internal static AccountServerConfig ForTests(Uri baseUrl, string anonKey) => new(baseUrl, anonKey);

    public Uri Function(string name) => new(BaseUrl, "functions/v1/" + name);

    /// <summary>The anon key is not a secret of this PC, but it is still kept out of logs and ToString.</summary>
    public override string ToString() => BaseUrl.GetLeftPart(UriPartial.Authority);
}
