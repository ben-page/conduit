namespace Conduit.Attributes;

/// <summary>
/// Prevents this event from being broadcast to any transport.
/// The event dispatches only to local listeners within the same process.
/// Use for sensitive events (PII, security, internal state) that must
/// not leave the service boundary.
/// </summary>
[AttributeUsage(AttributeTargets.Class)]
public sealed class LocalOnlyAttribute : Attribute;
