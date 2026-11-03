using System.Globalization;
using Conduit.Exceptions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Conduit.Runtime;

/// <summary>
/// Writes a refusal's message and details in the reader's language, at the edge where an answer leaves the server for
/// a person: the dispatch endpoint (<see cref="RenderResult{T}"/>), the SignalR hub (<see cref="RenderResult{T}"/>) and
/// REST problem details (<see cref="RenderMessage"/>). Everything in process stays English: a caller of
/// <c>HandleWireRequestAsync</c> or <c>PreflightWireRequestAsync</c> gets the English message with
/// <see cref="DispatchError.Text"/> carrying the sentence's name and values. A host that hands such an answer to a person
/// calls <see cref="RenderResult{T}"/> at its own edge, or rethrows a refusal carrying the <c>Text</c> so its own response
/// writes it once. Every member falls back to the throw site's English, never throws, and logs a failing resolver or
/// renderer at Error.
/// </summary>
public static class RefusalRendering
{
    private static readonly Action<ILogger, string, string, Exception?> LogRenderFailed =
        LoggerMessage.Define<string, string>(LogLevel.Error, new EventId(20, "RefusalTextRenderFailed"),
            "The refusal text renderer threw rendering '{TextName}' for culture '{Culture}'; keeping the English message");

    private static readonly Action<ILogger, Exception?> LogCultureResolveFailed =
        LoggerMessage.Define(LogLevel.Error, new EventId(21, "DispatchCultureResolveFailed"),
            "The dispatch culture resolver threw; answering in English");

    /// <summary><paramref name="result"/> with its error's message and details written in the reader's language. The
    /// same instance, with no service lookup, when there is no error or neither the error nor any detail carries a
    /// <see cref="RefusalText"/>.</summary>
    public static DispatchResult<T> RenderResult<T>(
        DispatchResult<T> result, IServiceProvider services, MessageContext? context)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(services);
        var error = result.Error;
        if (error is null || (error.Text is null && !HasDetailText(error.Details)))
        {
            return result;
        }

        var (renderer, culture, logger) = Prepare(services, context, FirstTextName(error));
        return result with
        {
            Error = error with
            {
                Message = Render(error.Text, error.Message, renderer, culture, logger),
                Details = RenderDetails(error.Details, renderer, culture, logger),
            },
        };
    }

    /// <summary>The sentence for <paramref name="text"/> in the reader's language, or <paramref name="english"/> when
    /// <paramref name="text"/> is null (no service lookup) or anything declines or fails.</summary>
    public static string RenderMessage(
        RefusalText? text, string english, IServiceProvider services, MessageContext? context)
    {
        ArgumentNullException.ThrowIfNull(services);
        if (text is null)
        {
            return english;
        }

        var (renderer, culture, logger) = Prepare(services, context, text.Name);
        return Render(text, english, renderer, culture, logger);
    }

    private static string FirstTextName(DispatchError error)
    {
        if (error.Text is not null)
        {
            return error.Text.Name;
        }

        foreach (var detail in error.Details!)
        {
            if (detail.Text is not null)
            {
                return detail.Text.Name;
            }
        }

        return string.Empty;
    }

    private static bool HasDetailText(IReadOnlyList<DispatchErrorDetail>? details)
    {
        if (details is null)
        {
            return false;
        }

        foreach (var detail in details)
        {
            if (detail.Text is not null)
            {
                return true;
            }
        }

        return false;
    }

    // Run only when there is text to render. Resolving the services is inside the guard, so a resolver or renderer
    // that cannot be built answers English like one that throws.
#pragma warning disable CA1031 // A faulty resolver or renderer must not turn a refusal into a different failure.
    private static (IRefusalTextRenderer? Renderer, CultureInfo? Culture, ILogger Logger) Prepare(
        IServiceProvider services, MessageContext? context, string textName)
    {
        ILogger logger;
        try
        {
            logger = services.GetService<ILoggerFactory>()?.CreateLogger(typeof(RefusalRendering)) ?? NullLogger.Instance;
        }
        catch (Exception)
        {
            logger = NullLogger.Instance;
        }

        CultureInfo? culture;
        try
        {
            culture = services.GetService<IDispatchCultureResolver>()?.Resolve(context);
        }
        catch (Exception failure)
        {
            LogCultureResolveFailed(logger, failure);
            return (null, null, logger);
        }

        if (culture is null)
        {
            return (null, null, logger);
        }

        try
        {
            return (services.GetService<IRefusalTextRenderer>(), culture, logger);
        }
        catch (Exception failure)
        {
            LogRenderFailed(logger, textName, culture.Name, failure);
            return (null, null, logger);
        }
    }
#pragma warning restore CA1031

    /// <summary>The reader's culture for <paramref name="context"/>, or <see langword="null"/> (English) when there is
    /// no resolver, it answers null, or it throws (logged).</summary>
    public static CultureInfo? ResolveCulture(IDispatchCultureResolver? resolver, MessageContext? context, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        if (resolver is null)
        {
            return null;
        }

#pragma warning disable CA1031 // A faulty resolver must not turn a refusal into a different failure.
        try
        {
            return resolver.Resolve(context);
        }
        catch (Exception failure)
#pragma warning restore CA1031
        {
            LogCultureResolveFailed(logger, failure);
            return null;
        }
    }

    /// <summary>The sentence for <paramref name="text"/> in <paramref name="culture"/>, or <paramref name="english"/>
    /// when there is no text, renderer or culture, or the renderer answers null or whitespace, or throws (logged).</summary>
    public static string Render(
        RefusalText? text, string english, IRefusalTextRenderer? renderer, CultureInfo? culture, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        if (text is null || renderer is null || culture is null)
        {
            return english;
        }

#pragma warning disable CA1031 // A faulty renderer must not turn a refusal into a different failure.
        try
        {
            var rendered = renderer.Render(text, culture);
            return string.IsNullOrWhiteSpace(rendered) ? english : rendered;
        }
        catch (Exception failure)
#pragma warning restore CA1031
        {
            LogRenderFailed(logger, text.Name, culture.Name, failure);
            return english;
        }
    }

    /// <summary>Each detail rendered on its own: a detail with <see cref="DispatchErrorDetail.Text"/> becomes a copy with
    /// its message rendered; the others, and any whose rendering fails, are kept as they are. The list given is never
    /// changed; with nothing to render it is returned as it is.</summary>
    public static IReadOnlyList<DispatchErrorDetail>? RenderDetails(
        IReadOnlyList<DispatchErrorDetail>? details, IRefusalTextRenderer? renderer, CultureInfo? culture, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        if (details is null || renderer is null || culture is null)
        {
            return details;
        }

        var rendered = new List<DispatchErrorDetail>(details.Count);
        foreach (var detail in details)
        {
            var message = Render(detail.Text, detail.Message, renderer, culture, logger);
            rendered.Add(ReferenceEquals(message, detail.Message) ? detail : detail with { Message = message });
        }

        return rendered;
    }
}
