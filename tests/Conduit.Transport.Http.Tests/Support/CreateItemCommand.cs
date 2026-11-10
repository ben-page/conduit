using Conduit.Messages;

namespace Conduit.Transport.Http.Tests.Support;

public record CreateItemCommand(string Name) : ICommand<Guid>;
