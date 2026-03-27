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
/// Two binding strategies:
/// <list type="bullet">
///   <item><c>*Route</c> methods use <c>[AsParameters]</c> — bind from route/query (GET, DELETE)</item>
///   <item><c>*Body</c> methods bind the entire type from JSON body (POST, PUT, PATCH)</item>
/// </list>
/// Queries always use route binding. Commands have both variants.
/// </remarks>
public static class ConduitRestHandlers
{
    /// <summary>Maps a query type to a RESTful endpoint using route/query parameter binding.</summary>
    public static RouteHandlerBuilder MapQuery<TQuery, TResult>(
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
                    return ToProblem(ex);
                }
            });
    }

    /// <summary>Maps a command-with-result type to a RESTful endpoint using JSON body binding.</summary>
    public static RouteHandlerBuilder MapCommandBody<TCommand, TResult>(
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
                    return ToProblem(ex);
                }
            });
    }

    /// <summary>Maps a command-with-result type to a RESTful endpoint using route/query parameter binding.</summary>
    public static RouteHandlerBuilder MapCommandRoute<TCommand, TResult>(
        IEndpointRouteBuilder endpoints,
        string pattern,
        string method)
        where TCommand : ICommand<TResult>
    {
        return endpoints.MapMethods(pattern, [method],
            async ([AsParameters] TCommand command, IMediator mediator, CancellationToken cancellationToken) =>
            {
                try
                {
                    var result = await mediator.ExecuteAsync(command, cancellationToken);
                    return Results.Ok(result);
                }
                catch (ConduitException ex)
                {
                    return ToProblem(ex);
                }
            });
    }

    /// <summary>Maps a void command type to a RESTful endpoint using JSON body binding.</summary>
    public static RouteHandlerBuilder MapVoidCommandBody<TCommand>(
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
                    return ToProblem(ex);
                }
            });
    }

    /// <summary>Maps a void command type to a RESTful endpoint using route/query parameter binding.</summary>
    public static RouteHandlerBuilder MapVoidCommandRoute<TCommand>(
        IEndpointRouteBuilder endpoints,
        string pattern,
        string method)
        where TCommand : ICommand
    {
        return endpoints.MapMethods(pattern, [method],
            async ([AsParameters] TCommand command, IMediator mediator, CancellationToken cancellationToken) =>
            {
                try
                {
                    await mediator.ExecuteAsync(command, cancellationToken);
                    return Results.NoContent();
                }
                catch (ConduitException ex)
                {
                    return ToProblem(ex);
                }
            });
    }

    private static IResult ToProblem(ConduitException ex)
    {
        return Results.Problem(
            detail: ex.Message,
            statusCode: ex.Code.HttpStatusCode,
            title: ex.Code.Value);
    }
}
