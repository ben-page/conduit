using Conduit.Runtime;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;

namespace Conduit.Transport.Http;

/// <summary>Opts a minimal-API route into Conduit's inbound dispatch context: before the route's handler runs, the
/// registered <see cref="IDispatchContextEnricher"/>s run over the request's user and the context starts exactly as
/// it does for Conduit's own HTTP transports, so an in-process <see cref="IMediator"/> dispatch from the handler
/// carries the same server-trusted properties (e.g. the user id) as the hub, the wire endpoint and a REST-mapped
/// command.</summary>
public static class DispatchContextEndpointConventionBuilderExtensions
{
    /// <summary>Wraps the endpoint's request delegate so the inbound dispatch context starts before the route runs
    /// (so it applies to every endpoint kind, and runs before parameter binding), and tags the endpoint with
    /// <see cref="ConduitDispatchContextMetadata"/> so a host can verify the opt-in. Applied to a route group
    /// (<c>app.MapGroup("").WithConduitDispatchContext()</c>) it opts every route in the group, nested groups
    /// included, in.</summary>
    /// <remarks>This deliberately is not an endpoint filter: ASP.NET Core's filtered invocation skips the route
    /// handler when the response status is already 400 as the endpoint runs. An OpenIddict client with
    /// <c>EnableErrorPassthrough()</c> pre-sets 400 on its callback and sign-out routes and relies on the handler
    /// running to override it, so a filter would answer 400 where the handler answers 302.</remarks>
    /// <param name="builder">The route or route group to opt in.</param>
    /// <typeparam name="TBuilder">The convention builder type, returned for chaining.</typeparam>
    /// <returns>The same builder.</returns>
    public static TBuilder WithConduitDispatchContext<TBuilder>(this TBuilder builder)
        where TBuilder : IEndpointConventionBuilder
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.WithMetadata(ConduitDispatchContextMetadata.Instance);
        builder.Finally(static endpointBuilder =>
        {
            if (endpointBuilder.RequestDelegate is not { } inner)
            {
                return;
            }

            // async on purpose: the AsyncLocal context set inside must not flow back out to the middleware after the endpoint.
            endpointBuilder.RequestDelegate = async context =>
            {
                HttpDispatchContext.Start(context, context.RequestServices.GetServices<IDispatchContextEnricher>());
                await inner(context);
            };
        });
        return builder;
    }
}
