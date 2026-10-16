using Conduit.Attributes;

namespace Conduit.Audit.Tests.Support;

/// <summary>A body with a redacted member beside many strings, for the payload cap: the placeholder must survive a deep cut.</summary>
public sealed record RedactedNote([AuditRedacted] string Ssn, string Title, IReadOnlyList<string> Lines);
