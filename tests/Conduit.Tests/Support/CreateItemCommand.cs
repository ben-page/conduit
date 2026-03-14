using Conduit.Messages;

namespace Conduit.Tests.Support;

public record CreateItemCommand(string Name) : ICommand<Guid>;
