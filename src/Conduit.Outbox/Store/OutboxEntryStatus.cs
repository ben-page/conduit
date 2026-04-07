namespace Conduit.Outbox.Store;

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
}
