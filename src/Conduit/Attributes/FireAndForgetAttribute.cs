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
/// — these require a response. The source generator emits CONDUIT002 if misapplied.</item>
/// <item>Pipeline behaviors run in the caller's context before enqueueing.</item>
/// <item>Handler exceptions are logged, not propagated to the caller.</item>
/// <item>Over a transport, uses <c>SendEventAsync</c> (no reply infrastructure).</item>
/// </list>
/// </remarks>
[AttributeUsage(AttributeTargets.Class)]
public sealed class FireAndForgetAttribute : Attribute;
