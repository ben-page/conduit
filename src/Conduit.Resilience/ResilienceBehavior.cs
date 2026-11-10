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
    /// The pipeline key used when no type-specific or namespace pipeline is registered.
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

    /// <summary>
    /// Set on every <see cref="ResilienceContext"/> <see cref="ResilienceBehavior{TRequest,TResult}"/> executes a
    /// pipeline with, so <see cref="RetryObservationListener"/> observes only the retries of a Conduit dispatch.
    /// </summary>
    internal static readonly ResiliencePropertyKey<bool> DispatchKey = new("Conduit.Dispatch");

    /// <summary>
    /// The key of the pipeline registered for one message type: <c>"Conduit.Type:"</c> and its full name. Conduit's
    /// keys share the host's <see cref="ResiliencePipelineRegistry{TKey}"/>, so each carries a prefix no pipeline the
    /// host registers for its own use is named with by accident.
    /// </summary>
    internal static string TypePipelineKey(Type messageType) => "Conduit.Type:" + messageType.FullName;

    /// <summary>The key of the pipeline registered for a namespace: <c>"Conduit.Namespace:"</c> and the namespace.</summary>
    internal static string NamespacePipelineKey(string ns) => "Conduit.Namespace:" + ns;

    /// <summary>
    /// The keys a pipeline for <paramref name="requestType"/> is looked up under, most specific first: its type key,
    /// then the namespace key of each enclosing namespace on whole segments from the longest (<c>A.B.C</c>, <c>A.B</c>,
    /// <c>A</c>), then <see cref="DefaultPipelineKey"/>.
    /// </summary>
    internal static string[] PipelineKeys(Type requestType)
    {
        var keys = new List<string> { TypePipelineKey(requestType) };
        for (var ns = requestType.Namespace; !string.IsNullOrEmpty(ns); ns = EnclosingNamespace(ns))
        {
            keys.Add(NamespacePipelineKey(ns));
        }

        keys.Add(DefaultPipelineKey);
        return [.. keys];
    }

    private static string? EnclosingNamespace(string ns)
    {
        var lastDot = ns.LastIndexOf('.');
        return lastDot < 0 ? null : ns[..lastDot];
    }
}

/// <summary>
/// <see cref="IPipelineBehavior{TRequest,TResult}"/> that wraps dispatch in a Polly v8 resilience pipeline. The
/// pipeline is the first one registered under, in order: the request type's key (<c>"Conduit.Type:"</c> + its full
/// name), the key of its namespace or of the longest registered enclosing namespace (<c>"Conduit.Namespace:"</c> + the
/// namespace), and the default key <c>"Conduit.Default"</c>. It looks up no other key. Under each key a typed
/// <see cref="ResiliencePipeline{T}"/> is preferred to a non-generic <see cref="ResiliencePipeline"/>. If none is
/// registered, the next delegate is called directly.
/// </summary>
/// <typeparam name="TRequest">The command or query type.</typeparam>
/// <typeparam name="TResult">The result type returned by the handler.</typeparam>
public sealed class ResilienceBehavior<TRequest, TResult> : IPipelineBehavior<TRequest, TResult>
{
    // The lookup order is a pure function of TRequest, so it is computed once per closed type. What is registered under
    // each key is asked of the provider on every dispatch: the provider belongs to one host (and caches the pipelines it
    // builds), and a static cache of the resolved pipeline would hand one host's pipeline to another in the same process.
    private static readonly string[] PipelineKeys = ResilienceBehavior.PipelineKeys(typeof(TRequest));

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

        if (!this.TryResolvePipeline(out var typedPipeline, out var pipeline))
        {
            return await next();
        }

        var context = ResilienceContextPool.Shared.Get(cancellationToken);
        try
        {
            EnrichContext(context);
            return typedPipeline is not null
                ? await typedPipeline.ExecuteAsync(static async (_, nextStep) => await nextStep(), context, next)
                : await pipeline!.ExecuteAsync(static async (_, nextStep) => await nextStep(), context, next);
        }
        finally
        {
            ResilienceContextPool.Shared.Return(context);
        }
    }

    /// <summary>
    /// Walks <see cref="PipelineKeys"/>, most specific first, and takes the first key with a pipeline: a typed
    /// <see cref="ResiliencePipeline{T}"/> (registered via <c>AddResiliencePipeline&lt;string, TResult&gt;</c>), else a
    /// non-generic <see cref="ResiliencePipeline"/> (registered via <c>AddResiliencePipeline(key, builder =&gt; ...)</c>,
    /// as <see cref="ConduitResilienceBuilder"/> does).
    /// </summary>
    private bool TryResolvePipeline(out ResiliencePipeline<TResult>? typedPipeline, out ResiliencePipeline? pipeline)
    {
        foreach (var key in PipelineKeys)
        {
            if (this.pipelineProvider.TryGetPipeline<TResult>(key, out typedPipeline))
            {
                pipeline = null;
                return true;
            }

            if (this.pipelineProvider.TryGetPipeline(key, out pipeline))
            {
                typedPipeline = null;
                return true;
            }
        }

        typedPipeline = null;
        pipeline = null;
        return false;
    }

    private static void EnrichContext(ResilienceContext context)
    {
        context.Properties.Set(ResilienceBehavior.DispatchKey, true);

        var messageContext = MessageContext.Current;
        if (messageContext is null)
        {
            return;
        }

        context.Properties.Set(ResilienceBehavior.MessageIdKey, messageContext.MessageId);
        context.Properties.Set(ResilienceBehavior.ConversationIdKey, messageContext.ConversationId);
    }
}
