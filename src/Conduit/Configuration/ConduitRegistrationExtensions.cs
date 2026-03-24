using Microsoft.Extensions.DependencyInjection;

namespace Conduit.Configuration;

/// <summary>
/// Extension methods for overriding Conduit's auto-registered handlers and type registry.
/// Call after <c>AddConduit()</c>.
/// </summary>
public static class ConduitRegistrationExtensions
{
    /// <summary>
    /// Overrides which message types this host handles by removing auto-registered
    /// handlers and setting exclusion filters on the type registry. Must be called
    /// after <c>AddConduit()</c>.
    /// </summary>
    /// <remarks>
    /// Use this to:
    /// <list type="bullet">
    /// <item>Simulate separate applications within a single test assembly</item>
    /// <item>Replace real handlers with test doubles</item>
    /// <item>Prevent a host from creating broker topology for types it doesn't handle</item>
    /// </list>
    /// </remarks>
    /// <param name="services">The service collection (after AddConduit has been called).</param>
    /// <param name="configure">Callback to configure overrides via the fluent builder.</param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection ConfigureConduitRegistrations(
        this IServiceCollection services,
        Action<ConduitRegistrationOverrides> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);

        var overrides = new ConduitRegistrationOverrides(services);
        configure(overrides);
        overrides.Apply();

        return services;
    }
}
