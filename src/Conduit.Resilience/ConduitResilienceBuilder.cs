using Microsoft.Extensions.DependencyInjection;
using Polly;

namespace Conduit.Resilience;

/// <summary>
/// Fluent builder for configuring Polly resilience pipelines for Conduit dispatch.
/// Pipelines are registered in the DI container under string keys and resolved
/// by <see cref="ResilienceBehavior{TRequest,TResult}"/> at dispatch time.
/// </summary>
public class ConduitResilienceBuilder
{
    /// <summary>
    /// The underlying service collection where resilience pipelines are registered.
    /// </summary>
    internal IServiceCollection Services { get; }

    /// <summary>
    /// Initializes a new instance of <see cref="ConduitResilienceBuilder"/>.
    /// </summary>
    /// <param name="services">The service collection to register pipelines into.</param>
    public ConduitResilienceBuilder(IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        this.Services = services;
    }

    /// <summary>
    /// Registers a catch-all resilience pipeline under the key
    /// <see cref="ResilienceBehavior.DefaultPipelineKey"/> (<c>"Conduit.Default"</c>).
    /// Applied to any dispatch for which no type-specific or namespace pipeline is registered.
    /// </summary>
    /// <param name="configure">Callback to build the resilience pipeline.</param>
    /// <returns>This builder for chaining.</returns>
    public ConduitResilienceBuilder AddDefaultPipeline(
        Action<ResiliencePipelineBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        this.Services.AddResiliencePipeline(ResilienceBehavior.DefaultPipelineKey, configure);
        return this;
    }

    /// <summary>
    /// Registers a resilience pipeline scoped to a specific request type <typeparamref name="T"/>.
    /// The pipeline key is <c>"Conduit.Type:"</c> + <c>typeof(T).FullName</c>. Takes precedence over namespace
    /// pipelines and the default pipeline.
    /// </summary>
    /// <typeparam name="T">The request type (command or query) to scope the pipeline to.</typeparam>
    /// <param name="configure">Callback to build the resilience pipeline.</param>
    /// <returns>This builder for chaining.</returns>
    public ConduitResilienceBuilder AddPipelineForType<T>(
        Action<ResiliencePipelineBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        this.Services.AddResiliencePipeline(ResilienceBehavior.TypePipelineKey(typeof(T)), configure);
        return this;
    }

    /// <summary>
    /// Registers a resilience pipeline for every message type in the namespace <paramref name="namespacePrefix"/> and
    /// its sub-namespaces, matched on whole segments: <c>"MyApp.Orders"</c> covers <c>MyApp.Orders.CreateOrder</c> and
    /// <c>MyApp.Orders.Commands.CancelOrder</c>, never <c>MyApp.OrdersArchive.Purge</c>. When several registered
    /// namespaces enclose a type, the longest wins. A pipeline registered for the type itself takes precedence, and
    /// any namespace pipeline takes precedence over the default. The pipeline key is <c>"Conduit.Namespace:"</c> + the
    /// namespace, so a pipeline the host registers for its own use under a name equal to a namespace is never applied.
    /// </summary>
    /// <param name="namespacePrefix">
    /// The namespace to match (e.g. <c>"MyApp.Orders"</c>).
    /// </param>
    /// <param name="configure">Callback to build the resilience pipeline.</param>
    /// <returns>This builder for chaining.</returns>
    public ConduitResilienceBuilder AddPipelineForNamespace(
        string namespacePrefix,
        Action<ResiliencePipelineBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(namespacePrefix);
        ArgumentNullException.ThrowIfNull(configure);
        this.Services.AddResiliencePipeline(ResilienceBehavior.NamespacePipelineKey(namespacePrefix), configure);
        return this;
    }
}
