using System.Globalization;
using Conduit.Exceptions;
using Conduit.Runtime;

namespace Conduit.Transport.Http.Tests.Support;

/// <summary>
/// A stand-in renderer whose format tests read. It answers <see langword="null"/> for English
/// (<c>TwoLetterISOLanguageName == "en"</c>); otherwise <c>"es:" + Name</c>, and when the text has values, each one
/// appended in the order given as <c> key=value</c>, where a <see cref="TextName"/> value is written as its
/// <see cref="TextName.Name"/> (so a test sees the catalog was asked to translate it) and any other value with
/// <see cref="Convert.ToString(object?, IFormatProvider?)"/> in the invariant culture. A null value is written empty.
/// </summary>
public sealed class SpanishRefusalRenderer : IRefusalTextRenderer
{
    public string? Render(RefusalText text, CultureInfo culture)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(culture);
        if (culture.TwoLetterISOLanguageName == "en")
        {
            return null;
        }

        var rendered = "es:" + text.Name;
        foreach (var (key, value) in text.Values ?? new Dictionary<string, object?>())
        {
            var shown = value switch
            {
                TextName name => name.Name,
                null => string.Empty,
                _ => Convert.ToString(value, CultureInfo.InvariantCulture),
            };
            rendered += $" {key}={shown}";
        }

        return rendered;
    }
}
