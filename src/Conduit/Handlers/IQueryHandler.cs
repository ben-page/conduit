using Conduit.Messages;

namespace Conduit.Handlers;

/// <summary>
/// Handles a query that returns a result.
/// </summary>
/// <typeparam name="TQuery">The query type.</typeparam>
/// <typeparam name="TResult">The result type.</typeparam>
public interface IQueryHandler<TQuery, TResult> where TQuery : IQuery<TResult>
{
    /// <summary>
    /// Handles the query and returns a result.
    /// </summary>
    /// <param name="query">The query to handle.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The query result.</returns>
    public Task<TResult> HandleAsync(TQuery query, CancellationToken cancellationToken);
}
