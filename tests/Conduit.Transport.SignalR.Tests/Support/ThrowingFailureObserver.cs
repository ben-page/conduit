using Conduit.Runtime;

namespace Conduit.Transport.SignalR.Tests.Support;

/// <summary>An <see cref="IInboundFailureObserver"/> that throws for every failure, as one whose store is down.</summary>
public sealed class ThrowingFailureObserver : IInboundFailureObserver
{
    public ValueTask OnDispatchFailedAsync(string messageType, DispatchError error, CancellationToken cancellationToken)
        => throw new InvalidOperationException("The observer failed.");
}
