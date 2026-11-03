using System.Globalization;

namespace Conduit.Runtime;

/// <summary>
/// The ready-made <see cref="IDispatchCultureResolver"/>: one dispatch property, a fixed map of its values to
/// cultures. A value is matched case-insensitively and only when it is a string the map lists; anything else (no
/// context, no property, a non-string, an unmapped or empty or oversized string) answers <see langword="null"/>, which
/// means English. It never builds a <see cref="CultureInfo"/> from the value and never throws.
/// </summary>
/// <param name="propertyName">The <see cref="MessageContext.Properties"/> key that holds the language.</param>
/// <param name="cultures">The values this resolver accepts, and the culture each stands for.</param>
public sealed class DispatchPropertyCultureResolver(string propertyName, IReadOnlyDictionary<string, CultureInfo> cultures)
    : IDispatchCultureResolver
{
    private readonly string propertyName = propertyName ?? throw new ArgumentNullException(nameof(propertyName));

    private readonly Dictionary<string, CultureInfo> cultures = new(
        cultures ?? throw new ArgumentNullException(nameof(cultures)), StringComparer.OrdinalIgnoreCase);

    /// <inheritdoc />
    public CultureInfo? Resolve(MessageContext? context)
    {
        if (context is null
            || !context.Properties.TryGetValue(this.propertyName, out var value)
            || value is not string text)
        {
            return null;
        }

        return this.cultures.GetValueOrDefault(text);
    }
}
