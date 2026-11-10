using Conduit.Messages;

namespace Conduit.Transport.Http.Tests.Support;

/// <summary>
/// Event whose listener records the correlation ids and properties of the <c>MessageContext</c> it runs under, as
/// <see cref="CaptureContextCommand"/>'s handler does for a request.
/// </summary>
public record CaptureContextEvent : IEvent;
