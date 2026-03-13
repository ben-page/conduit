namespace Conduit;

/// <summary>
/// Central entry point for dispatching commands, queries, and events
/// through the Conduit pipeline.
/// </summary>
public interface IMediator
{
    /// <summary>
    /// Dispatches a void command through the pipeline to its handler.
    /// </summary>
    /// <param name="command">The command to execute.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    public Task ExecuteAsync(ICommand command, CancellationToken cancellationToken = default);

    /// <summary>
    /// Dispatches a command that returns a result through the pipeline to its handler.
    /// </summary>
    /// <typeparam name="TResult">The type returned by the command handler.</typeparam>
    /// <param name="command">The command to execute.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The result produced by the command handler.</returns>
    public Task<TResult> ExecuteAsync<TResult>(ICommand<TResult> command, CancellationToken cancellationToken = default);

    /// <summary>
    /// Dispatches a query through the pipeline to its handler.
    /// </summary>
    /// <typeparam name="TResult">The type returned by the query handler.</typeparam>
    /// <param name="query">The query to execute.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The result produced by the query handler.</returns>
    public Task<TResult> FetchAsync<TResult>(IQuery<TResult> query, CancellationToken cancellationToken = default);

    /// <summary>
    /// Publishes an event to all registered listeners with continue-on-failure semantics.
    /// </summary>
    /// <param name="event">The event to publish.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    public Task PublishAsync(IEvent @event, CancellationToken cancellationToken = default);
}
