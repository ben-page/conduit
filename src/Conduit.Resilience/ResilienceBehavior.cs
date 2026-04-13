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

        if (this.TryResolveTypedPipeline(out var typedPipeline))
        {
            var context = ResilienceContextPool.Shared.Get(cancellationToken);
            try
            {
                EnrichContext(context);
                var attempt = 0;
                return await typedPipeline!.ExecuteAsync(async _ =>
                {
                    attempt++;
                    try
                    {
                        return await next();
                    }
                    catch (Exception ex)
                    {
                        MessageContext.Observe(new RetryAttempted(
                            DateTimeOffset.UtcNow,
                            attempt,
                            ex.Message,
                            ex.GetType().Name,
                            null));
                        throw;
                    }
                }, context);
            }
            finally
            {
                ResilienceContextPool.Shared.Return(context);
            }
        }

        if (this.TryResolveNonGenericPipeline(out var nonGenericPipeline))
        {
            var context = ResilienceContextPool.Shared.Get(cancellationToken);
            try
            {
                EnrichContext(context);
                var attempt = 0;
                return await nonGenericPipeline!.ExecuteAsync(async _ =>
                {
                    attempt++;
                    try
                    {
                        return await next();
                    }
                    catch (Exception ex)
                    {
                        MessageContext.Observe(new RetryAttempted(
                            DateTimeOffset.UtcNow,
                            attempt,
                            ex.Message,
                            ex.GetType().Name,
                            null));
                        throw;
                    }
                }, context);
            }
            finally
            {
                ResilienceContextPool.Shared.Return(context);
            }
        }

        return await next();
    }

    /// <summary>
    /// Tries to resolve a typed <see cref="ResiliencePipeline{TResult}"/> by type key,
    /// then by the default key. Typed pipelines are registered via
    /// <c>AddResiliencePipeline&lt;string, TResult&gt;</c>.
    /// </summary>
    private bool TryResolveTypedPipeline(out ResiliencePipeline<TResult>? pipeline)
    {
        if (this.pipelineProvider.TryGetPipeline<TResult>(TypeKey, out pipeline))
        {
            return true;
        }

        if (this.pipelineProvider.TryGetPipeline<TResult>(ResilienceBehavior.DefaultPipelineKey, out pipeline))
        {
            return true;
        }

        pipeline = null;
        return false;
    }

    /// <summary>
    /// Tries to resolve a non-generic <see cref="ResiliencePipeline"/> by type key,
    /// then by the default key. Non-generic pipelines are registered via
    /// <c>AddResiliencePipeline(key, builder =&gt; ...)</c>, as used by
    /// <see cref="ConduitResilienceBuilder"/>.
    /// </summary>
    private bool TryResolveNonGenericPipeline(out ResiliencePipeline? pipeline)
    {
        if (this.pipelineProvider.TryGetPipeline(TypeKey, out pipeline))
        {
            return true;
        }

        if (this.pipelineProvider.TryGetPipeline(ResilienceBehavior.DefaultPipelineKey, out pipeline))
        {
            return true;
        }

        pipeline = null;
        return false;
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
