namespace Conduit.Attributes;

/// <summary>
/// Marks a type for TypeScript client code generation: the Conduit.TypeGen build task emits a
/// TypeScript interface for it (a message or a DTO it carries). It grants no inbound access —
/// use <c>AddTypeFilter</c> for access control. On an event it is also what lets a server-mode
/// SignalR hub push the event to browsers when no <c>IEventRecipientResolver</c> is registered.
/// </summary>
[AttributeUsage(AttributeTargets.Class)]
public sealed class ClientVisibleAttribute : Attribute;
