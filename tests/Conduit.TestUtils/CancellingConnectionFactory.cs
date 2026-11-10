using System.Data.Common;
using Conduit.Persistence;

namespace Conduit.TestUtils;

/// <summary>
/// Hands out connections from a real factory, and cancels a caller's token source as the store asks
/// for the connection after the ones it allows. The store opens one connection per statement, so this
/// lands a cancellation exactly between two statements of one store method: a host's stop arriving
/// after a claim's UPDATE committed but before its read-back.
/// </summary>
public sealed class CancellingConnectionFactory : IConduitConnectionFactory
{
    private readonly IConduitConnectionFactory inner;
    private readonly CancellationTokenSource cancellation;
    private readonly int allowedConnections;
    private int openedConnections;

    public CancellingConnectionFactory(
        IConduitConnectionFactory inner, CancellationTokenSource cancellation, int allowedConnections)
    {
        this.inner = inner;
        this.cancellation = cancellation;
        this.allowedConnections = allowedConnections;
    }

    public async Task<DbConnection> CreateConnectionAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.Increment(ref this.openedConnections) > this.allowedConnections)
        {
            await this.cancellation.CancelAsync();
        }

        return await this.inner.CreateConnectionAsync(cancellationToken);
    }
}
