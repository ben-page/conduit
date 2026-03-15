using Conduit.Exceptions;
using Conduit.Messages;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Conduit.Transport.Http;

/// <summary>
/// Internal typed handler helpers for RESTful endpoint mapping.
/// Called by generated <c>ConduitRestEndpoints</c> extension methods only.
/// </summary>
/// <remarks>
/// Queries use <c>[AsParameters]</c> for route/query string binding (GET/DELETE).
/// Commands bind from the JSON body directly (POST/PUT/PATCH).
/// </remarks>
internal static class ConduitRestHandlers
{
    internal static RouteHandlerBuilder MapQuery<TQuery, TResult>(
        IEndpointRouteBuilder endpoints,
        string pattern,
        string method)
        where TQuery : IQuery<TResult>
    {
        return endpoints.MapMethods(pattern, [method],
            async ([AsParameters] TQuery query, IMediator mediator, CancellationToken cancellationToken) =>
            {
                try
                {
                    var result = await mediator.FetchAsync(query, cancellationToken);
                    return Results.Ok(result);
                }
                catch (ConduitException ex)
                {
                    return Results.Problem(
                        detail: ex.Message,
                        statusCode: ex.Code.HttpStatusCode,
                        title: ex.Code.Value);
                }
            });
    }

    internal static RouteHandlerBuilder MapCommand<TCommand, TResult>(
        IEndpointRouteBuilder endpoints,
        string pattern,
        string method)
        where TCommand : ICommand<TResult>
    {
        return endpoints.MapMethods(pattern, [method],
            async (TCommand command, IMediator mediator, CancellationToken cancellationToken) =>
            {
                try
                {
                    var result = await mediator.ExecuteAsync(command, cancellationToken);
                    return Results.Ok(result);
                }
                catch (ConduitException ex)
                {
                    return Results.Problem(
                        detail: ex.Message,
                        statusCode: ex.Code.HttpStatusCode,
                        title: ex.Code.Value);
                }
            });
    }

    internal static RouteHandlerBuilder MapVoidCommand<TCommand>(
        IEndpointRouteBuilder endpoints,
        string pattern,
        string method)
        where TCommand : ICommand
    {
        return endpoints.MapMethods(pattern, [method],
            async (TCommand command, IMediator mediator, CancellationToken cancellationToken) =>
            {
                try
                {
                    await mediator.ExecuteAsync(command, cancellationToken);
                    return Results.NoContent();
                }
                catch (ConduitException ex)
                {
                    return Results.Problem(
                        detail: ex.Message,
                        statusCode: ex.Code.HttpStatusCode,
                        title: ex.Code.Value);
                }
            });
    }
}
