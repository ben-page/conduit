using System.Globalization;
using Conduit.Exceptions;
using Microsoft.Extensions.Logging;

namespace Conduit.Runtime;

/// <summary>
/// Writes a refusal's message and details in the reader's language where it becomes a response. Every member falls
/// back to the throw site's English, never throws, and logs a failing resolver or renderer at Error.
/// </summary>
public static class RefusalRendering
{
    private static readonly Action<ILogger, string, string, Exception?> LogRenderFailed =
        LoggerMessage.Define<string, string>(LogLevel.Error, new EventId(20, "RefusalTextRenderFailed"),
            "The refusal text renderer threw rendering '{TextName}' for culture '{Culture}'; keeping the English message");

    private static readonly Action<ILogger, Exception?> LogCultureResolveFailed =
        LoggerMessage.Define(LogLevel.Error, new EventId(21, "DispatchCultureResolveFailed"),
            "The dispatch culture resolver threw; answering in English");

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
