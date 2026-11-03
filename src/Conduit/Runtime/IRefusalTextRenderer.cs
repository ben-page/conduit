using System.Globalization;
using Conduit.Exceptions;

namespace Conduit.Runtime;

/// <summary>
/// Writes a refusal's named sentence in the reader's language. The host registers it as a service and owns the
/// catalog; Conduit owns neither a catalog nor a list of cultures.
/// </summary>
/// <remarks>
/// Conduit calls it for any culture its <see cref="IDispatchCultureResolver"/> resolves, English included, so a host
/// can keep one source of English (the throw site) by answering null. Null, an empty or whitespace sentence, or an
/// exception all leave the throw site's English message in place (an exception is also logged). Call it only where a
/// refusal becomes a response: the exception's own <see cref="Exception.Message"/> is never replaced.
/// </remarks>
public interface IRefusalTextRenderer
{
    /// <summary>The sentence for <paramref name="text"/> in <paramref name="culture"/>, or <see langword="null"/> to
    /// keep the throw site's English.</summary>
    public string? Render(RefusalText text, CultureInfo culture);
}
