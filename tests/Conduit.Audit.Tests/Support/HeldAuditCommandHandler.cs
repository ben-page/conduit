using Conduit.Handlers;

namespace Conduit.Audit.Tests.Support;

/// <summary>Handles <see cref="HeldAuditCommand"/>, waiting on the host's <see cref="AuditCommandGate"/> when it has one.</summary>
public sealed class HeldAuditCommandHandler(IServiceProvider services) : ICommandHandler<HeldAuditCommand, string>
{
    public async Task<string> HandleAsync(HeldAuditCommand command, CancellationToken cancellationToken)
    {
        if (services.GetService(typeof(AuditCommandGate)) is AuditCommandGate gate)
        {
            await gate.HoldAsync();
        }

        return command.Value;
    }
}
