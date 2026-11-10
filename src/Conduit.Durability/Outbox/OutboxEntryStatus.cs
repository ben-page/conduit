namespace Conduit.Durability.Outbox;

/// <summary>
/// Status of an outbox entry.
/// </summary>
public enum OutboxEntryStatus
{
    /// <summary>Message has not been sent yet.</summary>
    Pending = 0,
    /// <summary>Message was successfully sent to the broker.</summary>
    Sent = 1,
    /// <summary>Message failed to send after maximum retries.</summary>
    Failed = 2,
    /// <summary>
    /// Message has been atomically claimed by a worker (relay or immediate-send flush) and is
    /// being dispatched. A claim is exclusive — only the claiming worker will dispatch the row.
    /// A claim that is not resolved (sent/failed/retried) before the claim timeout is treated as
    /// abandoned and becomes re-claimable, so a crashed worker never strands a message.
    /// </summary>
    Claiming = 3,
}
