using AwesomeAssertions;

namespace Conduit.Tests;

[TestClass]
public class HandlerInterfaceTests
{
    private record CreateItemCommand(string Name) : ICommand<Guid>;
    private record DeleteItemCommand(Guid Id) : ICommand;
    private record GetItemQuery(Guid Id) : IQuery<string>;
    private record ItemCreatedEvent(Guid Id) : IEvent;

    private class CreateItemHandler : ICommandHandler<CreateItemCommand, Guid>
    {
        public Task<Guid> HandleAsync(CreateItemCommand command, CancellationToken cancellationToken)
            => Task.FromResult(Guid.NewGuid());
    }

    private class DeleteItemHandler : ICommandHandler<DeleteItemCommand>
    {
        public Task HandleAsync(DeleteItemCommand command, CancellationToken cancellationToken)
            => Task.CompletedTask;
    }

    private class GetItemHandler : IQueryHandler<GetItemQuery, string>
    {
        public Task<string> HandleAsync(GetItemQuery query, CancellationToken cancellationToken)
            => Task.FromResult("item");
    }

    private class ItemCreatedListener : IEventListener<ItemCreatedEvent>
    {
        public Task HandleAsync(ItemCreatedEvent @event, CancellationToken cancellationToken)
            => Task.CompletedTask;
    }

    [TestMethod]
    public async Task CommandHandler_WithResult_ReturnsValue()
    {
        var handler = new CreateItemHandler();
        var result = await handler.HandleAsync(new CreateItemCommand("test"), CancellationToken.None);
        result.Should().NotBeEmpty();
    }

    [TestMethod]
    public async Task CommandHandler_Void_Completes()
    {
        var handler = new DeleteItemHandler();
        await handler.HandleAsync(new DeleteItemCommand(Guid.NewGuid()), CancellationToken.None);
    }

    [TestMethod]
    public async Task QueryHandler_ReturnsValue()
    {
        var handler = new GetItemHandler();
        var result = await handler.HandleAsync(new GetItemQuery(Guid.NewGuid()), CancellationToken.None);
        result.Should().Be("item");
    }

    [TestMethod]
    public async Task EventListener_Completes()
    {
        var listener = new ItemCreatedListener();
        await listener.HandleAsync(new ItemCreatedEvent(Guid.NewGuid()), CancellationToken.None);
    }
}
