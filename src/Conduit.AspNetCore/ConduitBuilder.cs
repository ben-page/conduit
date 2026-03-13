using Microsoft.Extensions.DependencyInjection;

namespace Conduit.AspNetCore;

/// <summary>
/// Fluent builder for configuring Conduit services during DI registration.
/// </summary>
public class ConduitBuilder
{
    /// <summary>
    /// The underlying service collection.
    /// </summary>
    internal IServiceCollection Services { get; }

    /// <summary>
    /// Initializes a new instance of the <see cref="ConduitBuilder"/> class.
    /// </summary>
    /// <param name="services">The service collection to configure.</param>
    public ConduitBuilder(IServiceCollection services)
    {
        this.Services = services;
    }
}
