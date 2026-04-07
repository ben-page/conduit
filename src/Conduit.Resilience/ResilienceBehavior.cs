using Conduit.Middleware;
using Conduit.Runtime;
using Polly;
using Polly.Registry;

namespace Conduit.Resilience;

/// <summary>
/// Shared constants and property keys for <see cref="ResilienceBehavior{TRequest,TResult}"/>.
/// Placed on a non-generic type so callers can reference them without type parameters.
/// </summary>
public static class ResilienceBehavior
{
    /// <summary>
    /// The pipeline key used when no type-specific pipeline is registered.
    /// Register a pipeline under this key to apply it as a catch-all fallback.
    /// </summary>
    internal const string DefaultPipelineKey = "Conduit.Default";

    /// <summary>
    /// Resilience context property key for <see cref="MessageContext.MessageId"/>.
    /// Available inside <c>OnRetry</c>, <c>OnCircuitOpened</c>, and similar callbacks.
    /// </summary>
    public static readonly ResiliencePropertyKey<string?> MessageIdKey =
        new("Conduit.MessageId");

    /// <summary>
    /// Resilience context property key for <see cref="MessageContext.ConversationId"/>.
    /// Available inside <c>OnRetry</c>, <c>OnCircuitOpened</c>, and similar callbacks.
    /// </summary>
    public static readonly ResiliencePropertyKey<string?> ConversationIdKey =
        new("Conduit.ConversationId");
}

/// <summary>
/// <see cref="IPipelineBehavior{TRequest,TResult}"/> that wraps dispatch in a Polly v8
/// resilience pipeline. Pipelines are resolved by type name first, then by the default
/// key <c>"Conduit.Default"</c>. If neither is found, the next delegate is called directly.
/// </summary>
/// <typeparam name="TRequest">The command or query type.</typeparam>
/// <typeparam name="TResult">The result type returned by the handler.</typeparam>
public sealed class ResilienceBehavior<TRequest, TResult> : IPipelineBehavior<TRequest, TResult>
{
    private static readonly string TypeKey = typeof(TRequest).FullName!;

    private readonly ResiliencePipelineProvider<string> pipelineProvider;

    /// <summary>
    /// Initializes a new instance of <see cref="ResilienceBehavior{TRequest,TResult}"/>.
    /// </summary>
    /// <param name="pipelineProvider">The Polly pipeline provider resolved from DI.</param>
    public ResilienceBehavior(ResiliencePipelineProvider<string> pipelineProvider)
    {
        this.pipelineProvider = pipelineProvider;
    }

    /// <inheritdoc/>
    public async Task<TResult> HandleAsync(
        TRequest request,
        Func<Task<TResult>> next,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(next);

        var pipeline = this.ResolvePipeline();

        if (pipeline is null)
        {
            return await next();
        }

        var context = ResilienceContextPool.Shared.Get(cancellationToken);

        try
        {
            EnrichContext(context);
            return await pipeline.ExecuteAsync(
                async _ => await next(),
                context);
        }
        finally
        {
            ResilienceContextPool.Shared.Return(context);
        }
    }

    private ResiliencePipeline<TResult>? ResolvePipeline()
    {
        if (this.pipelineProvider.TryGetPipeline<TResult>(TypeKey, out var typePipeline))
        {
            return typePipeline;
        }

        if (this.pipelineProvider.TryGetPipeline<TResult>(ResilienceBehavior.DefaultPipelineKey, out var defaultPipeline))
        {
            return defaultPipeline;
        }

        return null;
    }

    private static void EnrichContext(ResilienceContext context)
    {
        var messageContext = MessageContext.Current;
        if (messageContext is null)
        {
            return;
        }

        context.Properties.Set(ResilienceBehavior.MessageIdKey, messageContext.MessageId);
        context.Properties.Set(ResilienceBehavior.ConversationIdKey, messageContext.ConversationId);
    }
}
