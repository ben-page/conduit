using Conduit.Exceptions;
using Conduit.Messages;
using Conduit.Runtime;
using Conduit.Transport.Http.Internal;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

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
                    HttpDispatchContext.Start(httpContext, enrichers);
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
                try
                {
                    var command = IsMultipart(request)
                        ? await MultipartCommandBinder.BindAsync<TCommand>(request, cancellationToken)
                        : (await request.ReadFromJsonAsync<TCommand>(cancellationToken))!;

                    HttpDispatchContext.Start(request.HttpContext, enrichers);
                    var result = await mediator.ExecuteAsync(command, cancellationToken);
                    return ToHttpResult(result);
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
                    HttpDispatchContext.Start(httpContext, enrichers);
                    var result = await mediator.ExecuteAsync(command, cancellationToken);
                    return ToHttpResult(result);
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
                try
                {
                    var command = IsMultipart(request)
                        ? await MultipartCommandBinder.BindAsync<TCommand>(request, cancellationToken)
                        : (await request.ReadFromJsonAsync<TCommand>(cancellationToken))!;

                    HttpDispatchContext.Start(request.HttpContext, enrichers);
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
                    HttpDispatchContext.Start(httpContext, enrichers);
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

    private static IResult ToProblem(ConduitException ex, HttpContext httpContext)
    {
        // Resolved here, on the failure path only; a host that registers neither keeps today's English detail.
        var services = httpContext.RequestServices;
        var logger = services.GetService<ILoggerFactory>()?.CreateLogger(typeof(ConduitRestHandlers))
            ?? NullLogger.Instance;
        var culture = RefusalRendering.ResolveCulture(
            services.GetService<IDispatchCultureResolver>(), MessageContext.Current, logger);
        var detail = RefusalRendering.Render(
            ex.Text, ex.Message, services.GetService<IRefusalTextRenderer>(), culture, logger);

        return Results.Problem(
            detail: detail,
            statusCode: ex.Code.HttpStatusCode,
            title: ex.Code.Value);
    }

    /// <summary>
    /// Maps a handler result to an <see cref="IResult"/>. <see cref="BinaryData"/>
    /// and <see cref="ConduitFile"/> produce file responses; <c>null</c> produces 404;
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
        _ => Results.Ok(result),
    };
}
