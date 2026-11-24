using System.Net;
using System.Security.Cryptography.X509Certificates;
using Nexus.Core.Security;

namespace Nexus.Agent.Server;

internal sealed class AgentIdentity(Guid pcId, string pcName, X509Certificate2 certificate, Nexus.Agent.Security.IDeviceKey? deviceKey = null)
{
    /// <summary>The permanent PC key PcId is derived from; null only for identities built by hand in unit tests.</summary>
    public Nexus.Agent.Security.IDeviceKey? DeviceKey { get; } = deviceKey;
    public Guid PcId { get; } = pcId;
    public string PcName { get; } = pcName;
    public X509Certificate2 Certificate { get; } = certificate;
    public string Fingerprint { get; } = DeviceCrypto.CertificateFingerprint(certificate);
    public string Version { get; } = typeof(AgentIdentity).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

    /// <summary>Filled in once Kestrel has bound its sockets.</summary>
    public IReadOnlyList<IPEndPoint> Endpoints { get; set; } = [];
}
