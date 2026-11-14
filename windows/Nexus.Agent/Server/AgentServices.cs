using Nexus.Core.Power;
using Nexus.Agent.Session;
using Nexus.Agent.Storage;
using Nexus.Core.Pairing;
using Nexus.Core.Protocol;
using Nexus.Core.Security;

namespace Nexus.Agent.Server;

/// <summary>Everything a connection needs, wired once per agent process.</summary>
internal sealed class AgentServices
{
    public AgentServices(AgentIdentity identity, IDeviceStore devices, IPowerController power, AuditLog audit, TimeProvider time, TimeSpan? pairingLifetime = null, TimeSpan? handshakeTimeout = null)
    {
        Identity = identity;
        Devices = devices;
        Power = power;
        Audit = audit;
        Time = time;
        PairingLifetime = pairingLifetime ?? PairingManager.DefaultLifetime;
        HandshakeTimeout = handshakeTimeout ?? ConnectionSession.DefaultHandshakeTimeout;
        Pairing = new PairingManager(devices, time);
        Validator = CommandValidator.CreateDefault(time);
        Trays = new TrayRegistry();
        Dispatcher = new CommandDispatcher(this);
    }

    public AgentIdentity Identity { get; }
    public IDeviceStore Devices { get; }
    public IPowerController Power { get; }
    public AuditLog Audit { get; }
    public TimeProvider Time { get; }
    public TimeSpan PairingLifetime { get; }
    public TimeSpan HandshakeTimeout { get; }
    public PairingManager Pairing { get; }
    public CommandValidator Validator { get; }
    public CommandDispatcher Dispatcher { get; }
    public TrayRegistry Trays { get; }
}
