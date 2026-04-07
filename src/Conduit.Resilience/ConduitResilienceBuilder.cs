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
    /// Applied to any dispatch for which no type-specific pipeline is registered.
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
    /// The pipeline key is <c>typeof(T).FullName</c>. Takes precedence over the default pipeline.
    /// </summary>
    /// <typeparam name="T">The request type (command or query) to scope the pipeline to.</typeparam>
    /// <param name="configure">Callback to build the resilience pipeline.</param>
    /// <returns>This builder for chaining.</returns>
    public ConduitResilienceBuilder AddPipelineForType<T>(
        Action<ResiliencePipelineBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        this.Services.AddResiliencePipeline(typeof(T).FullName!, configure);
        return this;
    }

    /// <summary>
    /// Registers a resilience pipeline for all request types whose full name starts with
    /// <paramref name="namespacePrefix"/>. Applied at dispatch time when resolving a pipeline
    /// for a type in the given namespace. The pipeline key is the namespace prefix itself.
    /// </summary>
    /// <param name="namespacePrefix">
    /// The namespace prefix to match (e.g. <c>"MyApp.Orders"</c>).
    /// </param>
    /// <param name="configure">Callback to build the resilience pipeline.</param>
    /// <returns>This builder for chaining.</returns>
    public ConduitResilienceBuilder AddPipelineForNamespace(
        string namespacePrefix,
        Action<ResiliencePipelineBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(namespacePrefix);
        ArgumentNullException.ThrowIfNull(configure);
        this.Services.AddResiliencePipeline(namespacePrefix, configure);
        return this;
    }
}
