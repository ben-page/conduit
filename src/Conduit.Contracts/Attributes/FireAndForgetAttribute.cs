namespace Conduit.Attributes;

/// <summary>
/// Marks a void command or event for fire-and-forget dispatch.
/// The caller returns immediately — the handler runs in the background
/// via <c>ConduitBackgroundDispatcher</c>.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>Only valid on <c>ICommand</c> (void) and <c>IEvent</c>.</item>
/// <item>Not valid on <c>ICommand&lt;TResult&gt;</c> or <c>IQuery&lt;TResult&gt;</c>
/// — these require a response. The source generator emits CONDUIT003 if misapplied.</item>
/// <item>Pipeline behaviors run in the caller's context before enqueueing.</item>
/// <item>Handler exceptions are logged, not propagated to the caller.</item>
/// <item>Over a transport, uses <c>SendEventAsync</c> (no reply infrastructure).</item>
/// <item>When an outbox is registered, non-<c>Immediate</c> fire-and-forget messages are
/// captured transactionally and delivered post-commit; see docs/outbox.md.</item>
/// </list>
/// </remarks>
[AttributeUsage(AttributeTargets.Class)]
public sealed class FireAndForgetAttribute : Attribute
{
    /// <summary>
    /// Bypasses transactional outbox capture for this type: the message is enqueued to local
    /// listeners and sent to transports immediately at publish time, even when an outbound
    /// interceptor (the outbox) is registered and a database transaction is ambient. Declare this
    /// only for messages that must survive their publishing command's rollback — the event analog
    /// of a write-then-throw side effect. Delivery is at-most-once, exactly as plain
    /// fire-and-forget was before the outbox.
    /// </summary>
    public bool Immediate { get; init; }
}
