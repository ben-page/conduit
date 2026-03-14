using Conduit.Dispatch;

namespace Conduit.Tests.Support;

public class SpyTransport : IConduitTransport
{
    public List<DispatchEnvelope> SentRequests { get; } = [];
    public List<DispatchEnvelope> SentEvents { get; } = [];

    public object? RequestResult { get; set; }

    public Task<object?> SendRequestAsync(DispatchEnvelope envelope, CancellationToken cancellationToken)
    {
        this.SentRequests.Add(envelope);
        return Task.FromResult(this.RequestResult);
    }

    public Task SendEventAsync(DispatchEnvelope envelope, CancellationToken cancellationToken)
    {
        this.SentEvents.Add(envelope);
        return Task.CompletedTask;
    }
}
