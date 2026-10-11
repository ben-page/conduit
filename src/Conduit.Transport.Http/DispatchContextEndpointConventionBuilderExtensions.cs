using Conduit.Runtime;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Conduit.Transport.Http;

/// <summary>Opts a minimal-API route into Conduit's inbound dispatch context: before the route's handler runs, the
/// registered <see cref="IDispatchContextEnricher"/>s run over the request's user and the context starts exactly as
/// it does for Conduit's own HTTP transports, so an in-process <see cref="IMediator"/> dispatch from the handler
/// carries the same server-trusted properties (e.g. the user id) as the hub, the wire endpoint and a REST-mapped
/// command.</summary>
public static class DispatchContextEndpointConventionBuilderExtensions
{
    /// <summary>Adds the endpoint filter that starts the inbound dispatch context before the route runs, and tags the
    /// endpoint with <see cref="ConduitDispatchContextMetadata"/> so a host can verify the opt-in. Applied to a route
    /// group (<c>app.MapGroup("").WithConduitDispatchContext()</c>) it opts every route in the group, nested groups
    /// included, in.</summary>
    /// <param name="builder">The route or route group to opt in.</param>
    /// <typeparam name="TBuilder">The convention builder type, returned for chaining.</typeparam>
    /// <returns>The same builder.</returns>
    public static TBuilder WithConduitDispatchContext<TBuilder>(this TBuilder builder)
        where TBuilder : IEndpointConventionBuilder
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.WithMetadata(ConduitDispatchContextMetadata.Instance);
        return builder.AddEndpointFilter(static async (context, next) =>
        {
            HttpDispatchContext.Start(
                context.HttpContext,
                context.HttpContext.RequestServices.GetServices<IDispatchContextEnricher>());
            return await next(context);
        });
    }
}
