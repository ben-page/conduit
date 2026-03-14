using Conduit.Attributes;
using Conduit.Messages;

[assembly: DispatchableNamespace("Conduit.Transport.SignalR.Tests.TestApp")]

namespace Conduit.Transport.SignalR.Tests.TestApp;

[Dispatchable]
public record CreateItemCommand(string Name) : ICommand<Guid>;

[Dispatchable]
public record DeleteItemCommand(Guid Id) : ICommand;

[Dispatchable]
public record GetItemQuery(Guid Id) : IQuery<string>;

[Dispatchable]
public record ItemCreatedEvent(Guid Id, string Name) : IEvent;

[Dispatchable]
public record ExplodingCommand() : ICommand;
