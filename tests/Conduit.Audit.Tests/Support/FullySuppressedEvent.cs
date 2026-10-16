using Conduit.Messages;

namespace Conduit.Audit.Tests.Support;

/// <summary>Registered with <c>Suppress&lt;T&gt;()</c> by the tests that need a fully suppressed message.</summary>
public sealed record FullySuppressedEvent(string BirthDate) : IEvent;
