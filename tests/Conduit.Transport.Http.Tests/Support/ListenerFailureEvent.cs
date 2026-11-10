using Conduit.Messages;

namespace Conduit.Transport.Http.Tests.Support;

/// <summary>An event whose only listener, <see cref="FailingListener"/>, throws.</summary>
public sealed record ListenerFailureEvent(string Data) : IEvent;
