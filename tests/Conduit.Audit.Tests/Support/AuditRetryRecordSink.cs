using Conduit.Audit.Runtime;
using Conduit.Handlers;

namespace Conduit.Audit.Tests.Support;

/// <summary>A singleton that collects the audit records published as <see cref="AuditBatch"/>, in arrival order.</summary>
public sealed class AuditRetryRecordSink
{
    private readonly Lock gate = new();
    private readonly List<DispatchAuditRecord> records = [];

    public void Add(IEnumerable<DispatchAuditRecord> batch)
    {
        lock (this.gate)
        {
            this.records.AddRange(batch);
        }
    }

    /// <summary>The records of <paramref name="messageType"/> collected so far.</summary>
    public IReadOnlyList<DispatchAuditRecord> Of(Type messageType)
    {
        lock (this.gate)
        {
            return [.. this.records.Where(record => record.MessageType == messageType.FullName)];
        }
    }

    /// <summary>Polls until <paramref name="messageType"/> has at least <paramref name="expected"/> records, or throws.</summary>
    public async Task WaitForAsync(Type messageType, int expected)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (this.Of(messageType).Count < expected)
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException($"Expected {expected} record(s) of {messageType.Name} but saw {this.Of(messageType).Count}.");
            }

            await Task.Delay(10);
        }
    }
}

/// <summary>
/// Hands each published <see cref="AuditBatch"/> to the host's <see cref="AuditRetryRecordSink"/>, when it has one. Being
/// a listener of <see cref="AuditBatch"/> it also lets the generator route that event to local listeners.
/// </summary>
public sealed class AuditRetryBatchListener(IServiceProvider services) : IEventListener<AuditBatch>
{
    public Task HandleAsync(AuditBatch batch, CancellationToken cancellationToken)
    {
        if (services.GetService(typeof(AuditRetryRecordSink)) is AuditRetryRecordSink sink)
        {
            sink.Add(batch.Records);
        }

        return Task.CompletedTask;
    }
}
