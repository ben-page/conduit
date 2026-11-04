using System.Globalization;

namespace Conduit.Jobs.Runtime;

/// <summary>
/// The key <see cref="IJobs.TriggerNowAsync"/> writes for a manual run, <c>{schedule}@manual-{instant}</c>, and the one
/// reading of it: whether a run is manual. The ledger has no column for how a run started, so the key is the record;
/// both halves live here so the writer and the reader cannot drift.
/// </summary>
internal static class ManualRunKey
{
    private const string Marker = "@manual-";

    /// <summary>The key of a manual run of <paramref name="scheduleName"/> queued at <paramref name="now"/>.</summary>
    /// <param name="scheduleName">The schedule being run.</param>
    /// <param name="now">The instant of the trigger, from the injected <see cref="TimeProvider"/>.</param>
    /// <returns>The run's key.</returns>
    public static string Build(string scheduleName, DateTimeOffset now)
        => string.Create(
            CultureInfo.InvariantCulture,
            $"{scheduleName}{Marker}{now.UtcDateTime.ToString("O", CultureInfo.InvariantCulture)}");

    /// <summary>
    /// Whether <paramref name="jobKey"/> is a manual run's key for its own declaring name. An occurrence's key
    /// (<c>{schedule}@{instant}</c>), a start-up run's (<c>{schedule}@start-{instant}</c>) and a message-keyed trigger
    /// run's (<c>{trigger}@{messageId}</c>) never match; a coalescing trigger's run matches only when its coalesce key
    /// itself begins <c>manual-</c>.
    /// </summary>
    /// <param name="jobKey">The run's key.</param>
    /// <param name="scheduleName">The run's declaring schedule or trigger.</param>
    /// <returns><see langword="true"/> for a run <see cref="IJobs.TriggerNowAsync"/> queued.</returns>
    public static bool Is(string jobKey, string? scheduleName)
        => scheduleName is not null
            && jobKey.Length > scheduleName.Length + Marker.Length
            && jobKey.StartsWith(scheduleName, StringComparison.Ordinal)
            && jobKey.AsSpan(scheduleName.Length).StartsWith(Marker, StringComparison.Ordinal);
}
