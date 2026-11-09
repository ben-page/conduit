using Conduit.Attributes;

namespace Conduit.TypeGen.Tests.Sample.Inheritance;

[ClientVisible]
public abstract record AuditEventBase
{
    public string EventId { get; init; } = string.Empty;
}

public sealed record PublicAuditEvent : AuditEventBase
{
    public string UserId { get; init; } = string.Empty;
}

[LocalOnly]
public sealed record InternalAuditEvent : AuditEventBase
{
    public string SecretToken { get; init; } = string.Empty;
}

// [LocalOnly] is inherited: neither this type nor the one deriving from it, which carries no attribute of its own, is
// emitted.
[LocalOnly]
public record RestrictedAuditEvent : AuditEventBase
{
    public string RestrictedScope { get; init; } = string.Empty;
}

public sealed record InheritedRestrictedAuditEvent : RestrictedAuditEvent
{
    public string InheritedSecret { get; init; } = string.Empty;
}
