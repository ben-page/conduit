using Conduit.Messages;

namespace Conduit.TypeGen.Tests.Sample;

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

public record TestGetQuery(Guid Id) : IQuery<string>;

public record TestItemCreatedEvent(Guid Id) : IEvent;
