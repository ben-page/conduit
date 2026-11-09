using Conduit.Messages;

namespace Conduit.Transport.Http.Tests.Support;

public record SetItemStatusCommand(ItemStatus Status) : ICommand<string>;
