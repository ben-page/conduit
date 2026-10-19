namespace Conduit.Tests.Support;

/// <summary>
/// A request type only the retry counter test dispatches, so its <c>conduit.message.type</c> tag tells that test's
/// measurements apart from those of tests running in parallel on the shared static meter.
/// </summary>
public sealed record RetryCountedRequest;
