using Conduit.Dispatch;
using Conduit.Messages;

namespace Conduit;

/// <summary>
/// Central entry point for dispatching commands, queries, and events.
/// Implemented by the source-generated <c>ConduitMediator</c> class.
/// Application code injects this interface. Registered as scoped in DI.
/// </summary>
public interface IMediator
{
    /// <summary>
    /// Dispatches a void command through the pipeline to its handler.
    /// </summary>
    public Task ExecuteAsync(ICommand command, CancellationToken cancellationToken = default);

    /// <summary>
    /// Dispatches a command that returns a result through the pipeline to its handler.
    /// </summary>
    public Task<TResult> ExecuteAsync<TResult>(ICommand<TResult> command, CancellationToken cancellationToken = default);

    /// <summary>
    /// Dispatches a query through the pipeline to its handler.
    /// </summary>
    public Task<TResult> FetchAsync<TResult>(IQuery<TResult> query, CancellationToken cancellationToken = default);

    /// <summary>
    /// Publishes an event to all registered listeners with continue-on-failure semantics.
    /// </summary>
    public Task PublishAsync(IEvent @event, CancellationToken cancellationToken = default);
}

/// <summary>
/// Extended mediator interface for inbound wire dispatch. Adds methods for handling
/// requests and events arriving from transports (HTTP, SignalR). Transport packages
/// inject this interface; application code uses <see cref="IMediator"/> instead.
/// </summary>
public interface IWireMediator : IMediator
{
    /// <summary>
    /// Handles an inbound wire request. Chains registered
    /// <see cref="IDispatchInterceptor"/> instances, deserializes the payload,
    /// dispatches to the handler, and wraps the result in <see cref="DispatchResult{T}"/>.
    /// </summary>
    public Task<DispatchResult<object>> HandleWireRequestAsync(
        DispatchEnvelope envelope,
        CancellationToken cancellationToken);

    /// <summary>
    /// Handles an inbound wire event. Chains registered
    /// <see cref="IDispatchInterceptor"/> instances, deserializes the payload,
    /// and publishes to local listeners. Swallows exceptions (fire-and-forget).
    /// </summary>
    public Task HandleWireEventAsync(
        DispatchEnvelope envelope,
        CancellationToken cancellationToken);
}
