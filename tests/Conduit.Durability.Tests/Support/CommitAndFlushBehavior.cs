using Conduit.Durability.Outbox;
using Conduit.Durability.Runtime.Outbox;
using Conduit.Middleware;
using Conduit.TestUtils;

namespace Conduit.Durability.Tests.Support;

/// <summary>
/// Models a host's outermost transactional behavior for a <see cref="RelayCommand"/>: it opens a transaction, runs the
/// pipeline, commits, and flushes the scope's outbox, which hands the committed events to the background dispatcher.
/// </summary>
public sealed class CommitAndFlushBehavior<TRequest, TResult>(StubConnectionFactory connectionFactory, IOutboxFlusher flusher)
    : IPipelineBehavior<TRequest, TResult>
{
    public async Task<TResult> HandleAsync(TRequest request, Func<Task<TResult>> next, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(next);
        if (request is not RelayCommand)
        {
            return await next();
        }

        await connectionFactory.BeginTransactionAsync();
        var result = await next();
        await connectionFactory.CommitAsync();
        await flusher.FlushAsync(cancellationToken);
        return result;
    }
}
