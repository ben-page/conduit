using Conduit.Messages;

namespace Conduit.Transport.SignalR.Tests.Support;

/// <summary>A void command whose handler refuses with a named sentence, as a host's handler does.</summary>
public record ShippedOrderCommand : ICommand;
