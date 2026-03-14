using System.Text.Json;
using Conduit.Dispatch;

namespace Conduit.Tests.Support;

public class StubRouter : IDispatchRouter
{
    public object? RequestResult { get; set; }
    public Exception? RequestException { get; set; }
    public Exception? EventException { get; set; }

    public Task<object?> DispatchRequestAsync(string type, JsonElement payload, CancellationToken cancellationToken)
    {
        if (this.RequestException is not null)
        {
            throw this.RequestException;
        }

        return Task.FromResult(this.RequestResult);
    }

    public Task DispatchEventAsync(string type, JsonElement payload, CancellationToken cancellationToken)
    {
        if (this.EventException is not null)
        {
            throw this.EventException;
        }

        return Task.CompletedTask;
    }
}
