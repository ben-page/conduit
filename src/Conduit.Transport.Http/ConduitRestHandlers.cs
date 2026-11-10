using System.Text.Json;
using Conduit.Exceptions;
using Conduit.Messages;
using Conduit.Runtime;
using Conduit.Transport.Http.Internal;
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
            async ([AsParameters] TQuery query, HttpContext httpContext,
                IEnumerable<IDispatchContextEnricher> enrichers, IMediator mediator,
                CancellationToken cancellationToken) =>
            {
                try
                {
                    HttpDispatchContext.Start(httpContext, enrichers, trustCallerContext: false, readClientProperties: false);
                    var result = await mediator.FetchAsync(query, cancellationToken);
                    return ToHttpResult(result);
                }
                catch (ConduitException ex)
                {
                    return ToProblem(ex, httpContext);
                }
            });
    }

    /// <summary>Maps a command-with-result type to a RESTful endpoint using JSON body binding.</summary>
    public static RouteHandlerBuilder MapCommandBody<TCommand, TResult>(
        IEndpointRouteBuilder endpoints,
        string pattern,
        string method)
        where TCommand : class, ICommand<TResult>
    {
        // Validate once at mapping time so misuse fails at startup rather than first request.
        FilePropertyMetadata.Validate(typeof(TCommand));

        return endpoints.MapMethods(pattern, [method],
            async (HttpRequest request, IEnumerable<IDispatchContextEnricher> enrichers,
                IMediator mediator, CancellationToken cancellationToken) =>
            {
                var (command, refusal) = await ReadBodyAsync<TCommand>(request, cancellationToken);
                if (command is null)
                {
                    return refusal;
                }

                try
                {
                    HttpDispatchContext.Start(request.HttpContext, enrichers, trustCallerContext: false, readClientProperties: false);
                    var result = await mediator.ExecuteAsync(command, cancellationToken);
                    return ToCommandResult(result);
                }
                catch (ConduitException ex)
                {
                    return ToProblem(ex, request.HttpContext);
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
            async ([AsParameters] TCommand command, HttpContext httpContext,
                IEnumerable<IDispatchContextEnricher> enrichers, IMediator mediator,
                CancellationToken cancellationToken) =>
            {
                try
                {
                    HttpDispatchContext.Start(httpContext, enrichers, trustCallerContext: false, readClientProperties: false);
                    var result = await mediator.ExecuteAsync(command, cancellationToken);
                    return ToCommandResult(result);
                }
                catch (ConduitException ex)
                {
                    return ToProblem(ex, httpContext);
                }
            });
    }

    /// <summary>Maps a void command type to a RESTful endpoint using JSON body binding.</summary>
    public static RouteHandlerBuilder MapVoidCommandBody<TCommand>(
        IEndpointRouteBuilder endpoints,
        string pattern,
        string method)
        where TCommand : class, ICommand
    {
        FilePropertyMetadata.Validate(typeof(TCommand));

        return endpoints.MapMethods(pattern, [method],
            async (HttpRequest request, IEnumerable<IDispatchContextEnricher> enrichers,
                IMediator mediator, CancellationToken cancellationToken) =>
            {
                var (command, refusal) = await ReadBodyAsync<TCommand>(request, cancellationToken);
                if (command is null)
                {
                    return refusal;
                }

                try
                {
                    HttpDispatchContext.Start(request.HttpContext, enrichers, trustCallerContext: false, readClientProperties: false);
                    await mediator.ExecuteAsync(command, cancellationToken);
                    return Results.NoContent();
                }
                catch (ConduitException ex)
                {
                    return ToProblem(ex, request.HttpContext);
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
            async ([AsParameters] TCommand command, HttpContext httpContext,
                IEnumerable<IDispatchContextEnricher> enrichers, IMediator mediator,
                CancellationToken cancellationToken) =>
            {
                try
                {
                    HttpDispatchContext.Start(httpContext, enrichers, trustCallerContext: false, readClientProperties: false);
                    await mediator.ExecuteAsync(command, cancellationToken);
                    return Results.NoContent();
                }
                catch (ConduitException ex)
                {
                    return ToProblem(ex, httpContext);
                }
            });
    }

    private static bool IsMultipart(HttpRequest request) =>
        request.ContentType is not null
        && request.ContentType.StartsWith("multipart/form-data", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Reads a body-bound command, answering a body the caller got wrong as ASP.NET's own binding would: a content type
    /// that is neither JSON nor multipart is 415; malformed JSON, a literal <c>null</c>, a form field that does not
    /// convert, or a missing required file is a 400 Problem whose <c>detail</c> is the parser's message. Only the read
    /// is guarded: a handler's own <see cref="FormatException"/> is still the server's failure.
    /// </summary>
    private static async Task<(TCommand? Command, IResult? Refusal)> ReadBodyAsync<TCommand>(
        HttpRequest request,
        CancellationToken cancellationToken)
        where TCommand : class
    {
        try
        {
            if (IsMultipart(request))
            {
                return (await MultipartCommandBinder.BindAsync<TCommand>(request, cancellationToken), null);
            }

            if (!request.HasJsonContentType())
            {
                return (null, Results.Problem(
                    detail: "The request body must be JSON (application/json) or multipart/form-data.",
                    statusCode: StatusCodes.Status415UnsupportedMediaType,
                    title: "UnsupportedMediaType"));
            }

            var command = await request.ReadFromJsonAsync<TCommand>(ConduitRuntime.JsonOptions, cancellationToken);
            return command is null
                ? (null, ToProblem(new BadRequestException("The request body is null."), request.HttpContext))
                : (command, null);
        }
        catch (ConduitException ex)
        {
            return (null, ToProblem(ex, request.HttpContext));
        }
        catch (Exception ex) when (ex is JsonException or FormatException or InvalidCastException or OverflowException
            or InvalidDataException)
        {
            return (null, ToProblem(new BadRequestException(ex.Message), request.HttpContext));
        }
    }

    private static IResult ToProblem(ConduitException ex, HttpContext httpContext)
    {
        // The edge where the answer leaves the server: the detail is written in the reader's language here.
        return Results.Problem(
            detail: RefusalRendering.RenderMessage(ex.Text, ex.Message, httpContext.RequestServices, MessageContext.Current),
            statusCode: ex.Code.HttpStatusCode,
            title: ex.Code.Value);
    }

    /// <summary>
    /// A command's result: <c>null</c> is 204 No Content (the command ran and has nothing to return), never the 404 a
    /// query's null means; anything else as <see cref="ToHttpResult"/>.
    /// </summary>
    private static IResult ToCommandResult(object? result) =>
        result is null ? Results.NoContent() : ToHttpResult(result);

    /// <summary>
    /// Maps a handler result to an <see cref="IResult"/>. <see cref="BinaryData"/>
    /// and <see cref="ConduitFile"/> produce file responses; <c>null</c> produces 404 (a query found nothing);
    /// anything else flows through <see cref="Results.Ok"/> as JSON.
    /// </summary>
    private static IResult ToHttpResult(object? result) => result switch
    {
        null => Results.NotFound(),
        BinaryData bd => Results.File(
            bd.ToArray(),
            bd.MediaType ?? "application/octet-stream"),
        ConduitFile cf => Results.File(
            cf.Content.ToArray(),
            cf.Content.MediaType ?? "application/octet-stream",
            cf.FileName),
        _ => Results.Json(result, ConduitRuntime.JsonOptions),
    };
}
