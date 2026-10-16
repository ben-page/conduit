using Conduit.Messages;

namespace Conduit.Audit.Tests.Support;

/// <summary>Registered with <c>SuppressPayload&lt;T&gt;()</c> by the tests that need a body-suppressed message.</summary>
public sealed record PayloadSuppressedEvent(string BirthDate) : IEvent;
