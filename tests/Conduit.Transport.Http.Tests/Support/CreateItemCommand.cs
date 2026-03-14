using Conduit.Attributes;
using Conduit.Messages;

[assembly: DispatchableNamespace("Conduit.Transport.Http.Tests.Support")]

namespace Conduit.Transport.Http.Tests.Support;

[Dispatchable]
public record CreateItemCommand(string Name) : ICommand<Guid>;
