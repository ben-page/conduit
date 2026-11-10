using Microsoft.Extensions.Logging;

namespace Conduit.Durability.Runtime.Outbox;

/// <summary>
/// The log events the relay (<see cref="OutboxRelay"/>) and the post-commit flush
/// (<see cref="OutboxCapture.ClaimAndDeliverAsync"/>) share. Each is logged under the caller's category. EventIds are
/// a contract: never renumber one.
/// </summary>
internal static partial class OutboxLog
{
    /// <summary>A resolution of the entry found its claim gone, so nothing more is written for the entry.</summary>
    [LoggerMessage(EventId = 321, EventName = "ClaimLost", Level = LogLevel.Warning,
        Message = "Lost the claim on outbox entry {EntryId} (type: {Type}): another worker took it over after the claim " +
            "timeout, or it was already resolved, so this attempt's outcome was not recorded")]
    public static partial void ClaimLost(ILogger logger, long entryId, string type);

    /// <summary>Disposing an entry's delivery scope failed once the attempt was over; the entry is not retried for it.</summary>
    [LoggerMessage(EventId = 322, EventName = "OutboxScopeDisposeFailed", Level = LogLevel.Warning,
        Message = "Disposing the delivery scope of outbox entry {EntryId} (type: {Type}) failed. The attempt is over, " +
            "so this does not retry the entry")]
    public static partial void ScopeDisposeFailed(ILogger logger, long entryId, string type, Exception exception);

    /// <summary>An observer failed to record an observation of an entry whose outcome is already written.</summary>
    [LoggerMessage(EventId = 323, EventName = "OutboxObservationFailed", Level = LogLevel.Warning,
        Message = "An observer failed to record {Observation} for outbox entry {EntryId}. The entry's outcome stands")]
    public static partial void ObservationFailed(ILogger logger, string observation, long entryId, Exception exception);
}
