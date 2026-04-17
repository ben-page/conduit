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
