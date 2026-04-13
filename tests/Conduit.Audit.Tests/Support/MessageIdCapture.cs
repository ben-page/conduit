namespace Conduit.Audit.Tests.Support;

/// <summary>
/// Test helper that captures the MessageId from the dispatch context inside a handler.
/// Registered as a singleton so the test can read the captured value after dispatch.
/// </summary>
public sealed class MessageIdCapture
{
    public string? MessageId { get; set; }
}
