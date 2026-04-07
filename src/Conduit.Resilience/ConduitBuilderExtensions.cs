using Conduit.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Conduit.Resilience;

/// <summary>
/// Extension methods for <see cref="ConduitBuilder"/> to register resilience pipelines.
/// </summary>
public static class ConduitBuilderExtensions
{
    /// <summary>
    /// Adds Polly v8 resilience support to the Conduit dispatch pipeline.
    /// Registers <see cref="ResilienceBehavior{TRequest,TResult}"/> as the outermost
    /// pipeline behavior so retry/circuit-breaker wraps all user-registered behaviors.
    /// </summary>
    /// <param name="builder">The Conduit builder to configure.</param>
    /// <param name="configure">
    /// Optional callback to register named resilience pipelines via
    /// <see cref="ConduitResilienceBuilder"/>. If <see langword="null"/>, only the
    /// behavior is registered — useful when pipelines are added later or externally.
    /// </param>
    /// <returns>The original <paramref name="builder"/> for chaining.</returns>
    public static ConduitBuilder AddResilience(
        this ConduitBuilder builder,
        Action<ConduitResilienceBuilder>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);

        var resilienceBuilder = new ConduitResilienceBuilder(builder.Services);
        configure?.Invoke(resilienceBuilder);

        // Insert at position 0 so retry wraps all user-registered behaviors (outermost).
        builder.Options.PipelineTypes.Insert(0, typeof(ResilienceBehavior<,>));
        builder.Services.AddTransient(typeof(ResilienceBehavior<,>));

        return builder;
    }
}
