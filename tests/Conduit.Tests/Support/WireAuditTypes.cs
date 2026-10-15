using Conduit.Attributes;
using Conduit.Handlers;
using Conduit.Messages;

namespace Conduit.Tests.Support;

// Grouped as ObservationTestTypes.cs groups its types: one message family, each with its handler.

public sealed record SecretBearingCommand(string Name, [DoNotAudit] string Secret) : ICommand<string>;

public sealed class SecretBearingCommandHandler : ICommandHandler<SecretBearingCommand, string>
{
    public Task<string> HandleAsync(SecretBearingCommand command, CancellationToken cancellationToken)
        => Task.FromResult(command.Name);
}

[DoNotAudit]
public sealed record WholeSecretCommand(string Secret) : ICommand;

public sealed class WholeSecretCommandHandler : ICommandHandler<WholeSecretCommand>
{
    public Task HandleAsync(WholeSecretCommand command, CancellationToken cancellationToken)
        => Task.CompletedTask;
}

public sealed record SecretLine(string Label, [DoNotAudit] string Pin);

public sealed record NestedSecretCommand(IReadOnlyList<SecretLine> Lines) : ICommand;

public sealed class NestedSecretCommandHandler : ICommandHandler<NestedSecretCommand>
{
    public Task HandleAsync(NestedSecretCommand command, CancellationToken cancellationToken)
        => Task.CompletedTask;
}
