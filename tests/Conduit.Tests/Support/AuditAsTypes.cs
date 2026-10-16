using System.Text.Json;
using Conduit.Attributes;
using Conduit.Handlers;
using Conduit.Messages;

namespace Conduit.Tests.Support;

// Grouped as WireAuditTypes.cs groups its types: an envelope family whose items carry a serialized message, plus the
// inner message, each with its handler.

public sealed record AuditAsInnerCommand(string Name, [DoNotAudit] string? Pin, [AuditRedacted] string? Ssn) : ICommand;

public sealed class AuditAsInnerCommandHandler : ICommandHandler<AuditAsInnerCommand>
{
    public Task HandleAsync(AuditAsInnerCommand command, CancellationToken cancellationToken)
        => Task.CompletedTask;
}

public sealed record AuditAsItem(string? Type, [AuditAs(nameof(Type))] JsonElement Payload);

public sealed record AuditAsEnvelopeCommand(IReadOnlyList<AuditAsItem> Items) : ICommand;

public sealed class AuditAsEnvelopeCommandHandler : ICommandHandler<AuditAsEnvelopeCommand>
{
    public Task HandleAsync(AuditAsEnvelopeCommand command, CancellationToken cancellationToken)
        => Task.CompletedTask;
}

public sealed class AuditAsPropertyItem
{
    public string? Kind { get; init; }

    [AuditAs(nameof(Kind))]
    public JsonElement? Body { get; init; }
}
