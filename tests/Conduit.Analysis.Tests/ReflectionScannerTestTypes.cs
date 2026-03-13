using Conduit.Attributes;
using Conduit.Messages;

[assembly: DispatchableNamespace("Conduit.Analysis.Tests.Sample")]

namespace Conduit.Analysis.Tests.Sample.Commands
{
    public record TestCreateCommand(string Name) : ICommand<Guid>;
    public record TestDeleteCommand(Guid Id) : ICommand;

    public record TestValidatedCommand(string Name) : ICommand
    {
        public void Validate()
        {
            if (string.IsNullOrEmpty(Name))
            {
                throw new ArgumentException("Name required");
            }
        }
    }

    [NotDispatchable]
    public record TestExcludedCommand(string Data) : ICommand;
}

namespace Conduit.Analysis.Tests.Sample.Queries
{
    public record TestGetQuery(Guid Id) : IQuery<string>;
}

namespace Conduit.Analysis.Tests.Sample.Events
{
    public record TestItemCreatedEvent(Guid Id) : IEvent;
}

namespace Conduit.Analysis.Tests.Outside
{
    [Dispatchable]
    public record TestExplicitCommand(string Data) : ICommand;

    public record TestOrphanCommand(string Data) : ICommand;
}
