using Conduit.Runtime;
using Conduit.Messages;
using Conduit.Middleware;

namespace Conduit;

/// <summary>
/// Central entry point for dispatching commands, queries, and events.
/// Implemented by the source-generated <c>ConduitMediator</c> class.
/// Application code injects this interface. Registered as transient in DI
/// (stateless — handlers are resolved from <see cref="System.IServiceProvider"/> per call).
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
    /// Dispatches a void command through the pipeline to its handler with dispatch options.
    /// The <paramref name="configure"/> callback provides <see cref="DispatchOptions"/> for
    /// attaching properties used by audit enrichment and pipeline observers.
    /// </summary>
    /// <param name="command">The command to execute.</param>
    /// <param name="configure">Optional callback to configure dispatch options including properties for audit enrichment.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    public Task ExecuteAsync(ICommand command, Action<DispatchOptions>? configure, CancellationToken cancellationToken = default);

    /// <summary>
    /// Dispatches a command that returns a result through the pipeline to its handler.
    /// </summary>
    /// <typeparam name="TResult">The type returned by the command handler.</typeparam>
    /// <param name="command">The command to execute.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The result produced by the command handler.</returns>
    public Task<TResult> ExecuteAsync<TResult>(ICommand<TResult> command, CancellationToken cancellationToken = default);

    /// <summary>
    /// Dispatches a command that returns a result through the pipeline to its handler with dispatch options.
    /// The <paramref name="configure"/> callback provides <see cref="DispatchOptions"/> for
    /// attaching properties used by audit enrichment and pipeline observers.
    /// </summary>
    /// <typeparam name="TResult">The type returned by the command handler.</typeparam>
    /// <param name="command">The command to execute.</param>
    /// <param name="configure">Optional callback to configure dispatch options including properties for audit enrichment.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The result produced by the command handler.</returns>
    public Task<TResult> ExecuteAsync<TResult>(ICommand<TResult> command, Action<DispatchOptions>? configure, CancellationToken cancellationToken = default);

    /// <summary>
    /// Dispatches a query through the pipeline to its handler.
    /// </summary>
    /// <typeparam name="TResult">The type returned by the query handler.</typeparam>
    /// <param name="query">The query to execute.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The result produced by the query handler.</returns>
    public Task<TResult> FetchAsync<TResult>(IQuery<TResult> query, CancellationToken cancellationToken = default);

    /// <summary>
    /// Dispatches a query through the pipeline to its handler with dispatch options.
    /// The <paramref name="configure"/> callback provides <see cref="DispatchOptions"/> for
    /// attaching properties used by audit enrichment and pipeline observers.
    /// </summary>
    /// <typeparam name="TResult">The type returned by the query handler.</typeparam>
    /// <param name="query">The query to execute.</param>
    /// <param name="configure">Optional callback to configure dispatch options including properties for audit enrichment.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The result produced by the query handler.</returns>
    public Task<TResult> FetchAsync<TResult>(IQuery<TResult> query, Action<DispatchOptions>? configure, CancellationToken cancellationToken = default);

    /// <summary>
    /// Publishes an event to all registered listeners with continue-on-failure semantics.
    /// </summary>
    /// <param name="event">The event to publish.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    public Task PublishAsync(IEvent @event, CancellationToken cancellationToken = default);

    /// <summary>
    /// Publishes an event to all registered listeners with continue-on-failure semantics and dispatch options.
    /// The <paramref name="configure"/> callback provides <see cref="DispatchOptions"/> for
    /// attaching properties used by audit enrichment and pipeline observers.
    /// </summary>
    /// <param name="event">The event to publish.</param>
    /// <param name="configure">Optional callback to configure dispatch options including properties for audit enrichment.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    public Task PublishAsync(IEvent @event, Action<DispatchOptions>? configure, CancellationToken cancellationToken = default);
}

/// <summary>
/// Extended mediator interface for inbound wire dispatch. Adds methods for handling
/// requests and events arriving from transports (HTTP, SignalR). Transport packages
/// inject this interface; application code uses <see cref="IMediator"/> instead.
/// </summary>
public interface IWireMediator : IMediator
{
    /// <summary>
    /// Handles an inbound wire request. Runs registered <see cref="IInboundInterceptor"/>
    /// gates before deserialization, then dispatches to the handler and wraps the result
    /// in <see cref="DispatchResult{T}"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Call it from a transport, where no dispatch is running. Called from inside a running dispatch, the caller must
    /// first start a child context (<c>MessageContext.StartDispatch()</c>): otherwise the wire dispatch runs on the
    /// outer context, shares its MessageId and audit group, and its captured payload can replace the outer record's.
    /// Under a dispatch retry policy it also binds that context to the running attempt, so the outer dispatch's own
    /// audit record can be dropped with an abandoned attempt: an in-process wire call starts its own context.
    /// </para>
    /// </remarks>
    /// <param name="envelope">The wire envelope containing type name and JSON payload.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A dispatch result containing the handler output or error information. Only a cancellation of
    /// <paramref name="cancellationToken"/> throws (<see cref="OperationCanceledException"/>): the caller gave up, so
    /// there is no one to answer.</returns>
    public Task<DispatchResult<object>> HandleWireRequestAsync(
        DispatchEnvelope envelope,
        CancellationToken cancellationToken);

    /// <summary>
    /// Handles an inbound wire event. Runs registered <see cref="IInboundInterceptor"/>
    /// gates before deserialization, then publishes to local listeners. Throws
    /// <see cref="Exceptions.EventListenerException"/> when a listener fails, the <see cref="Exceptions.ConduitException"/>
    /// when the event is refused (a gate such as the type filter, or a type this host does not know) for the transport to
    /// answer its caller, and <see cref="OperationCanceledException"/> when <paramref name="cancellationToken"/> is
    /// cancelled. Any other failure -- outside the listeners: a pipeline behavior, the listeners' resolution, a gate that
    /// threw something other than a refusal -- is logged with a support reference and thrown as a
    /// <see cref="EventDispatchException"/> carrying it, for the transport to settle as an unexpected failure.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Call it from a transport, where no dispatch is running. Called from inside a running dispatch, the caller must
    /// first start a child context (<c>MessageContext.StartDispatch()</c>): otherwise the wire dispatch runs on the
    /// outer context, shares its MessageId and audit group, and its captured payload can replace the outer record's.
    /// Under a dispatch retry policy it also binds that context to the running attempt, so the outer dispatch's own
    /// audit record can be dropped with an abandoned attempt: an in-process wire call starts its own context.
    /// </para>
    /// </remarks>
    /// <param name="envelope">The wire envelope containing type name and JSON payload.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    public Task HandleWireEventAsync(
        DispatchEnvelope envelope,
        CancellationToken cancellationToken);
}
