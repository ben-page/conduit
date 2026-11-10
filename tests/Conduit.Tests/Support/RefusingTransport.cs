using Conduit.Runtime;

namespace Conduit.Tests.Support;

/// <summary>A broadcast transport whose every send fails, as a remote hub refusing a publish does.</summary>
public sealed class RefusingTransport : IConduitTransport
{
    public Task<object?> SendRequestAsync(DispatchEnvelope envelope, CancellationToken cancellationToken)
        => throw new InvalidOperationException("The remote refused the request.");

    public Task SendEventAsync(DispatchEnvelope envelope, CancellationToken cancellationToken)
        => throw new InvalidOperationException("The remote refused the publish.");

    public Task RedeliverAsync(DispatchEnvelope envelope, CancellationToken cancellationToken)
        => throw new InvalidOperationException("The remote refused the redelivery.");
}
