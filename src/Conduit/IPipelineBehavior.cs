namespace Conduit;

/// <summary>
/// Wraps the handler pipeline for commands and queries, enabling cross-cutting
/// concerns such as logging, validation, or transaction management.
/// Behaviors execute in registration order and form a Russian-doll middleware chain.
/// </summary>
/// <typeparam name="TRequest">The command or query type.</typeparam>
/// <typeparam name="TResult">The result type returned by the handler.</typeparam>
public interface IPipelineBehavior<TRequest, TResult>
{
    /// <summary>
    /// Executes the behavior. Call <paramref name="next"/> to invoke the next
    /// behavior in the pipeline or the handler itself.
    /// </summary>
    /// <param name="request">The incoming command or query.</param>
    /// <param name="next">A delegate that invokes the next behavior or the handler.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The result produced by the pipeline.</returns>
    public Task<TResult> HandleAsync(
        TRequest request,
        Func<Task<TResult>> next,
        CancellationToken cancellationToken);
}
