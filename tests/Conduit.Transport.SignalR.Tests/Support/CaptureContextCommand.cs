using Conduit.Messages;

namespace Conduit.Transport.SignalR.Tests.Support;

/// <summary>
/// Command whose handler records the correlation ids of the <c>MessageContext</c> it runs under, so tests can assert
/// which ids the hub honored and which it minted.
/// </summary>
public record CaptureContextCommand : ICommand<Guid>;
