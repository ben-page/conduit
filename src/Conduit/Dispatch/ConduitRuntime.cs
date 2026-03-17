using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Conduit.Configuration;
using Conduit.Exceptions;
using Conduit.Messages;
using Conduit.Middleware;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Conduit.Dispatch;

/// <summary>
/// Runtime helpers used by the source-generated mediator. Not intended for direct use.
/// </summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public static class ConduitRuntime
{
    /// <summary>
    /// Shared JSON serializer options used by the generated mediator for wire serialization.
    /// Uses camelCase naming and string enum conversion to match TypeGen output.
    /// </summary>
    public static JsonSerializerOptions JsonOptions { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };
    // Error-level logs
    private static readonly Action<ILogger, string, Exception?> LogDispatchError =
        LoggerMessage.Define<string>(LogLevel.Error, new EventId(1, "DispatchError"),
            "Unhandled exception dispatching '{Type}'");

    private static readonly Action<ILogger, string, Exception?> LogPublishError =
        LoggerMessage.Define<string>(LogLevel.Error, new EventId(2, "PublishError"),
            "Unhandled exception publishing event '{Type}'");

    private static readonly Action<ILogger, string, Exception?> LogListenerFailed =
        LoggerMessage.Define<string>(LogLevel.Error, new EventId(4, "ListenerFailed"),
            "Event listener '{Listener}' threw an exception");

    // Debug-level logs
    private static readonly Action<ILogger, string, Exception?> LogDispatchingLocally =
        LoggerMessage.Define<string>(LogLevel.Debug, new EventId(10, "DispatchingLocally"),
            "Dispatching '{Type}' locally");

    private static readonly Action<ILogger, string, string, Exception?> LogRoutingToEndpoint =
        LoggerMessage.Define<string, string>(LogLevel.Debug, new EventId(11, "RoutingToEndpoint"),
            "Routing '{Type}' to endpoint '{Endpoint}'");

    private static readonly Action<ILogger, string, int, Exception?> LogBroadcastingEvent =
        LoggerMessage.Define<string, int>(LogLevel.Debug, new EventId(12, "BroadcastingEvent"),
            "Broadcasting '{Type}' to {Count} transport(s)");

    private static readonly Action<ILogger, string, Exception?> LogWireRequestReceived =
        LoggerMessage.Define<string>(LogLevel.Debug, new EventId(13, "WireRequestReceived"),
            "Wire request received: '{Type}'");

    private static readonly Action<ILogger, string, Exception?> LogWireEventReceived =
        LoggerMessage.Define<string>(LogLevel.Debug, new EventId(14, "WireEventReceived"),
            "Wire event received: '{Type}'");

    private static readonly Action<ILogger, string, string, Exception?> LogForwardingWireRequest =
        LoggerMessage.Define<string, string>(LogLevel.Debug, new EventId(15, "ForwardingWireRequest"),
            "Forwarding wire request '{Type}' to endpoint '{Endpoint}'");

    private static readonly Action<ILogger, string, int, Exception?> LogExecutingPipelines =
        LoggerMessage.Define<string, int>(LogLevel.Debug, new EventId(16, "ExecutingPipelines"),
            "Executing {Count} pipeline behavior(s) for '{Type}'");
    /// <summary>
    /// Resolves the first route for the given message type. Returns the matched endpoint,
    /// or null for local dispatch. Used for commands and queries only — events broadcast
    /// to all DI-registered transports instead of routing.
    /// Throws if the matched endpoint is missing or has no transport configured.
    /// </summary>
    public static ConduitEndpoint? ResolveRoute(ConduitOptions options, string messageFullTypeName)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(messageFullTypeName);

        foreach (var route in options.Routes)
        {
            if (messageFullTypeName.StartsWith(route.Pattern, StringComparison.Ordinal))
            {
                if (!options.Endpoints.TryGetValue(route.EndpointName, out var endpoint))
                {
                    throw new InvalidOperationException(
                        $"Route pattern '{route.Pattern}' matched message '{messageFullTypeName}' " +
                        $"but endpoint '{route.EndpointName}' is not registered. " +
                        $"Ensure AddEndpoint(\"{route.EndpointName}\", ...) is called in your Conduit configuration.");
                }

                return endpoint;
            }
        }

        return null;
    }

    /// <summary>
    /// Resolves the transport for a named endpoint from DI using keyed services.
    /// Throws a descriptive error if no transport is registered for the endpoint.
    /// </summary>
    public static IConduitTransport ResolveTransport(IServiceProvider serviceProvider, ConduitEndpoint endpoint)
    {
        ArgumentNullException.ThrowIfNull(serviceProvider);
        ArgumentNullException.ThrowIfNull(endpoint);

        return serviceProvider.GetKeyedService<IConduitTransport>(endpoint.Name)
            ?? throw new InvalidOperationException(
                $"Endpoint '{endpoint.Name}' has no transport configured. " +
                $"Call UseHttp(), UseSignalR(), UseAzureServiceBus(), or UseRabbitMQ() in the endpoint configuration.");
    }

    /// <summary>
    /// Resolves pipeline behaviors in the order declared via
    /// <see cref="ConduitBuilder.AddPipeline"/>. Constructs closed generics
    /// from the ordered type list and resolves each from DI.
    /// Used for commands, queries, and events uniformly.
    /// </summary>
    public static IPipelineBehavior<TRequest, TResult>[] ResolvePipelines<TRequest, TResult>(
        ConduitOptions options,
        IServiceProvider serviceProvider)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(serviceProvider);

        if (options.PipelineTypes.Count == 0)
        {
            return [];
        }

        var behaviors = new IPipelineBehavior<TRequest, TResult>[options.PipelineTypes.Count];
        for (var i = 0; i < options.PipelineTypes.Count; i++)
        {
            var closedType = options.PipelineTypes[i]
                .MakeGenericType(typeof(TRequest), typeof(TResult));
            behaviors[i] = (IPipelineBehavior<TRequest, TResult>)serviceProvider.GetRequiredService(closedType);
        }

        return behaviors;
    }

    /// <summary>
    /// Chains pipeline behaviors around a handler call. Behaviors execute in
    /// registration order (first registered = outermost wrapper).
    /// </summary>
    public static async Task<TResult> ExecuteWithBehaviors<TRequest, TResult>(
        TRequest request,
        IPipelineBehavior<TRequest, TResult>[] behaviors,
        Func<Task<TResult>> handler,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(behaviors);
        ArgumentNullException.ThrowIfNull(handler);

        var next = handler;
        for (var i = behaviors.Length - 1; i >= 0; i--)
        {
            var behavior = behaviors[i];
            var currentNext = next;
            next = () => behavior.HandleAsync(request, currentNext, cancellationToken);
        }

        return await next();
    }

    /// <summary>
    /// Chains interceptors and executes a wire request dispatch.
    /// Returns DispatchResult with Ok/Fail wrapping.
    /// </summary>
    public static async Task<DispatchResult<object>> HandleWireRequest(
        DispatchEnvelope envelope,
        IEnumerable<IWireInterceptor> interceptors,
        Func<string, JsonElement, CancellationToken, Task<object?>> dispatch,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        LogWireRequestReceived(logger, envelope.Type, null);
        ArgumentNullException.ThrowIfNull(dispatch);

        var previousContext = MessageContext.Current;
        MessageContext.StartDispatch();

        Func<Task<object?>> chain = () =>
            dispatch(envelope.Type, envelope.Payload, cancellationToken);

        foreach (var interceptor in interceptors.Reverse())
        {
            var next = chain;
            chain = () => interceptor.InterceptRequestAsync(envelope, next, cancellationToken);
        }

        try
        {
            var result = await chain();
            return DispatchResult<object>.Ok(result);
        }
        catch (ConduitException ex)
        {
            return DispatchResult<object>.Fail(ex.Code.Value, ex.Message);
        }
#pragma warning disable CA1031
        catch (Exception ex)
#pragma warning restore CA1031
        {
            LogDispatchError(logger, envelope.Type, ex);
            return DispatchResult<object>.Fail("InternalError", "An unexpected error occurred");
        }
        finally
        {
            MessageContext.RestoreContext(previousContext);
        }
    }

    /// <summary>
    /// Chains interceptors and executes a wire event dispatch.
    /// Lets <see cref="EventListenerException"/> propagate to the transport for retry/dead-letter.
    /// Swallows all other exceptions.
    /// </summary>
    public static async Task HandleWireEvent(
        DispatchEnvelope envelope,
        IEnumerable<IWireInterceptor> interceptors,
        Func<string, JsonElement, CancellationToken, Task> dispatch,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        ArgumentNullException.ThrowIfNull(dispatch);
        LogWireEventReceived(logger, envelope.Type, null);

        var previousContext = MessageContext.Current;
        MessageContext.StartDispatch();

        Func<Task> chain = () =>
            dispatch(envelope.Type, envelope.Payload, cancellationToken);

        foreach (var interceptor in interceptors.Reverse())
        {
            var next = chain;
            chain = () => interceptor.InterceptEventAsync(envelope, next, cancellationToken);
        }

        try
        {
            await chain();
        }
        catch (EventListenerException)
        {
            throw;
        }
#pragma warning disable CA1031
        catch (Exception ex)
#pragma warning restore CA1031
        {
            LogPublishError(logger, envelope.Type, ex);
        }
        finally
        {
            MessageContext.RestoreContext(previousContext);
        }
    }

    /// <summary>
    /// Wraps a void Task as Task&lt;object&gt; for pipeline behavior compatibility.
    /// Returns <see cref="Unit.Value"/> so the result is never null and never boxes.
    /// </summary>
    public static async Task<object> VoidToObject(Func<Task> action)
    {
        ArgumentNullException.ThrowIfNull(action);

        await action();
        return Unit.Value;
    }

    /// <summary>
    /// Casts Task&lt;TSource&gt; to Task&lt;TTarget&gt; without allocating.
    /// Used by generic interface fallback methods.
    /// </summary>
    public static Task<TTarget> CastTask<TSource, TTarget>(Task<TSource> task)
    {
        return Unsafe.As<Task<TSource>, Task<TTarget>>(ref task);
    }

    /// <summary>
    /// Registers core Conduit infrastructure (options). Called by generated code.
    /// </summary>
    public static IServiceCollection AddConduitCore(
        IServiceCollection services,
        Action<ConduitBuilder>? configure = null)
    {
        var builder = new ConduitBuilder(services);
        configure?.Invoke(builder);

        ServiceCollectionServiceExtensions
            .AddSingleton(services, builder.Options);

        return services;
    }

    // Logging helpers called by generated mediator code.
    // These are thin wrappers around the LoggerMessage delegates above.

    /// <summary>Logs that a message is being dispatched locally.</summary>
    public static void LogLocalDispatch(ILogger logger, string typeName)
        => LogDispatchingLocally(logger, typeName, null);

    /// <summary>Logs that a message is being routed to a remote endpoint.</summary>
    public static void LogRouteDispatch(ILogger logger, string typeName, string endpointName)
        => LogRoutingToEndpoint(logger, typeName, endpointName, null);

    /// <summary>Logs that an event is being broadcast to transports.</summary>
    public static void LogBroadcast(ILogger logger, string typeName, int transportCount)
        => LogBroadcastingEvent(logger, typeName, transportCount, null);

    /// <summary>Logs that a wire request is being forwarded to a route.</summary>
    public static void LogWireForward(ILogger logger, string typeName, string endpointName)
        => LogForwardingWireRequest(logger, typeName, endpointName, null);

    /// <summary>Logs pipeline behavior count for a dispatch.</summary>
    public static void LogPipelines(ILogger logger, string typeName, int count)
        => LogExecutingPipelines(logger, typeName, count, null);

    /// <summary>Logs that an individual event listener threw an exception.</summary>
    public static void LogListenerFailure(ILogger logger, string listenerName, Exception exception)
        => LogListenerFailed(logger, listenerName, exception);
}
