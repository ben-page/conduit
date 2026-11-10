using Conduit.Messages;

namespace Conduit.Transport.Http.Tests.Support;

/// <summary>Takes an item out of the store, answering its name, or null when there was none.</summary>
public record TakeItemCommand(Guid Id) : ICommand<string?>;
