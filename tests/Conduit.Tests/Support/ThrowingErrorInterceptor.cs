using Conduit.Middleware;

namespace Conduit.Tests.Support;

/// <summary>
/// An <see cref="IInboundErrorInterceptor"/> that throws for every failure it is offered, as one whose store is
/// unreachable and that does not guard itself might.
/// </summary>
public sealed class ThrowingErrorInterceptor : IInboundErrorInterceptor
{
    public Task<bool> TryCaptureAsync(
        string messageType,
        BinaryData body,
        string sourceEndpoint,
        Exception exception,
        string? reference,
        CancellationToken cancellationToken)
        => throw new InvalidOperationException("The store is unreachable.");
}
