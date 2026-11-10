namespace Conduit.Middleware;

/// <summary>
/// Wraps the dispatch of a command, query or event (its handler, or an event's listeners),
/// enabling cross-cutting concerns such as logging, validation, or transaction management.
/// Behaviors execute in registration order (the first registered is outermost) and form a
/// Russian-doll middleware chain.
/// </summary>
/// <typeparam name="TRequest">The command, query or event type.</typeparam>
/// <typeparam name="TResult">The result type returned by the handler; <see cref="object"/> for a void command or an event.</typeparam>
public interface IPipelineBehavior<TRequest, TResult>
{
    /// <summary>
    /// Executes the behavior. Call <paramref name="next"/> to invoke the next
    /// behavior in the pipeline, or the handler (an event's listeners) itself.
    /// </summary>
    /// <param name="request">The incoming command, query or event.</param>
    /// <param name="next">A delegate that invokes the next behavior, or the handler or listeners.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The result produced by the pipeline.</returns>
    public Task<TResult> HandleAsync(
        TRequest request,
        Func<Task<TResult>> next,
        CancellationToken cancellationToken);
}
