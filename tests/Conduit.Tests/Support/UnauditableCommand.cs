using Conduit.Handlers;
using Conduit.Messages;

namespace Conduit.Tests.Support;

/// <summary>A command the audit trail cannot serialize: reading <see cref="Unreadable"/> throws.</summary>
public sealed record UnauditableCommand(string Value) : ICommand
{
    /// <summary>Throws, as a property whose getter fails does when the audit capture serializes the command.</summary>
    public string Unreadable => throw new InvalidOperationException($"The audit trail cannot read '{this.Value}'.");
}

public sealed class UnauditableCommandHandler : ICommandHandler<UnauditableCommand>
{
    public Task HandleAsync(UnauditableCommand command, CancellationToken cancellationToken) => Task.CompletedTask;
}
