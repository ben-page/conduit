using Conduit.Runtime;

namespace Conduit.Outbox.Tests.Support;

internal sealed class SpyTransport : IConduitTransport
{
    public List<DispatchEnvelope> SentEvents { get; } = [];
    public List<DispatchEnvelope> SentRequests { get; } = [];

    public Task<object?> SendRequestAsync(DispatchEnvelope envelope, CancellationToken cancellationToken)
    {
        this.SentRequests.Add(envelope);
        return Task.FromResult<object?>(null);
    }

    public Task SendEventAsync(DispatchEnvelope envelope, CancellationToken cancellationToken)
    {
        this.SentEvents.Add(envelope);
        return Task.CompletedTask;
    }
}

internal sealed class ThrowingTransport : IConduitTransport
{
    public Task<object?> SendRequestAsync(DispatchEnvelope envelope, CancellationToken cancellationToken)
    {
        throw new InvalidOperationException("Simulated transport failure");
    }

    public Task SendEventAsync(DispatchEnvelope envelope, CancellationToken cancellationToken)
    {
        throw new InvalidOperationException("Simulated transport failure");
    }
}
