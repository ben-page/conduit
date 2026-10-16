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

// An inner contract whose constructor refuses a value: reading it at capture throws ArgumentException.
public sealed record AuditAsThrowingInnerCommand : ICommand
{
    public AuditAsThrowingInnerCommand(string value)
    {
        if (value == "refused")
        {
            throw new ArgumentException("The value is refused.", nameof(value));
        }

        this.Value = value;
    }

    public string Value { get; }
}

public sealed class AuditAsThrowingInnerCommandHandler : ICommandHandler<AuditAsThrowingInnerCommand>
{
    public Task HandleAsync(AuditAsThrowingInnerCommand command, CancellationToken cancellationToken)
        => Task.CompletedTask;
}

public class AuditAsHiddenBase
{
    public int Type { get; init; }
}

// Hides the base's int Type with a string one: the [AuditAs] sibling is the most-derived declaration.
public sealed class AuditAsHiddenItem : AuditAsHiddenBase
{
    public new string? Type { get; init; }

    [AuditAs(nameof(Type))]
    public JsonElement Payload { get; init; }
}
