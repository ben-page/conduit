using Conduit.Messages;

namespace Conduit.Transport.SignalR.Tests.Support;

/// <summary>An event <see cref="ThrowingEventBehavior{TRequest, TResult}"/> fails before its listeners run.</summary>
public sealed record BehaviorFailureEvent(string Data) : IEvent;
