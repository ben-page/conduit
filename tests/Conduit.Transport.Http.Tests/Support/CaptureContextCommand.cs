using Conduit.Messages;

namespace Conduit.Transport.Http.Tests.Support;

/// <summary>
/// Command whose handler records the correlation ids and properties of the <c>MessageContext</c> it runs under,
/// so tests can assert which ids an inbound edge honored and which it minted.
/// </summary>
public record CaptureContextCommand : ICommand<Guid>;
