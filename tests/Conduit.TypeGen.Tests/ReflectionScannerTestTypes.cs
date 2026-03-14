using Conduit.Attributes;
using Conduit.Messages;

[assembly: DispatchableNamespace("Conduit.TypeGen.Tests.Sample")]

namespace Conduit.TypeGen.Tests;

public record TestCreateCommand(string Name) : ICommand<Guid>;
public record TestDeleteCommand(Guid Id) : ICommand;

public record TestValidatedCommand(string Name) : ICommand
{
    public void Validate()
    {
        if (string.IsNullOrEmpty(this.Name))
        {
            throw new ArgumentException("Name required");
        }
    }
}

[NotDispatchable]
public record TestExcludedCommand(string Data) : ICommand;

public record TestGetQuery(Guid Id) : IQuery<string>;

public record TestItemCreatedEvent(Guid Id) : IEvent;

[Dispatchable]
public record TestExplicitCommand(string Data) : ICommand;

public record TestOrphanCommand(string Data) : ICommand;
