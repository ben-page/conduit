using System.Globalization;

namespace Conduit.Durability.Persistence;

/// <summary>
/// The text the durability stores write a timestamp as, and bind a timestamp bound as: the UTC instant in the
/// round-trip ("O") format, <c>yyyy-MM-ddTHH:mm:ss.fffffffZ</c>. Every value then has one width and one suffix, so the
/// engines' text comparisons order them chronologically. A value written in its own offset (a local
/// <see cref="DateTime"/>, a <see cref="DateTimeOffset"/> at +01:00) would compare by its wall-clock digits instead,
/// so noon UTC written from a UTC+1 host would sort after 12:30 UTC.
/// </summary>
internal static class StoredTimestamp
{
    /// <summary>Formats an instant, whatever its offset, as UTC text.</summary>
    public static string Format(DateTimeOffset value)
        => value.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);

    /// <summary>
    /// Formats a <see cref="DateTime"/> as UTC text: a <see cref="DateTimeKind.Local"/> value is converted, and a
    /// <see cref="DateTimeKind.Unspecified"/> one is taken as UTC already, as the stores have always written it.
    /// </summary>
    public static string Format(DateTime value)
        => (value.Kind == DateTimeKind.Local ? value.ToUniversalTime() : DateTime.SpecifyKind(value, DateTimeKind.Utc))
            .ToString("O", CultureInfo.InvariantCulture);
}
