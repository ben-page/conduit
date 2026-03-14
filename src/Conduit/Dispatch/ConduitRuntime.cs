using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json;
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
    private static readonly Action<ILogger, string, Exception?> LogDispatchError =
        LoggerMessage.Define<string>(LogLevel.Error, new EventId(1, "DispatchError"),
            "Unhandled exception dispatching '{Type}'");

    private static readonly Action<ILogger, string, Exception?> LogPublishError =
        LoggerMessage.Define<string>(LogLevel.Error, new EventId(2, "PublishError"),
            "Unhandled exception publishing event '{Type}'");

    private static readonly Action<ILogger, Exception?> LogListenerError =
        LoggerMessage.Define(LogLevel.Error, new EventId(3, "ListenerError"),
            "Event listener or transport failed");
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

                if (endpoint.Transport is null)
                {
                    throw new InvalidOperationException(
                        $"Endpoint '{route.EndpointName}' has no transport configured. " +
                        $"Call UseHttp() or UseSignalR(uri) in the endpoint configuration.");
                }

                return endpoint;
            }
        }

        return null;
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
        ArgumentNullException.ThrowIfNull(dispatch);

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
            return DispatchResult<object>.Fail(ex.Code, ex.Message);
        }
#pragma warning disable CA1031
        catch (Exception ex)
#pragma warning restore CA1031
        {
            LogDispatchError(logger, envelope.Type, ex);
            return DispatchResult<object>.Fail("InternalError", "An unexpected error occurred");
        }
    }

    /// <summary>
    /// Chains interceptors and executes a wire event dispatch.
    /// Swallows all exceptions (fire-and-forget).
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
#pragma warning disable CA1031
        catch (Exception ex)
#pragma warning restore CA1031
        {
            LogPublishError(logger, envelope.Type, ex);
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
    /// Executes an action with error swallowing. Used for listener/transport calls
    /// where individual failures should not prevent other listeners from executing.
    /// </summary>
    public static async Task SafeExecute(Func<Task> action, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(action);

        try
        {
            await action();
        }
#pragma warning disable CA1031
        catch (Exception ex)
#pragma warning restore CA1031
        {
            LogListenerError(logger, ex);
        }
    }

    /// <summary>
    /// Registers core Conduit infrastructure (options). Called by generated code.
    /// </summary>
    public static Microsoft.Extensions.DependencyInjection.IServiceCollection AddConduitCore(
        Microsoft.Extensions.DependencyInjection.IServiceCollection services,
        Action<ConduitBuilder>? configure = null)
    {
        var builder = new ConduitBuilder(services);
        configure?.Invoke(builder);

        Microsoft.Extensions.DependencyInjection.ServiceCollectionServiceExtensions
            .AddSingleton(services, builder.Options);

        return services;
    }
}
