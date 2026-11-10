using Conduit.Messages;

namespace Conduit.Jobs.Tests.Support;

/// <summary>
/// The event the coalescing tests publish: a badge scanned at a location, whose summary is rebuilt from
/// <c>From</c> on.
/// </summary>
public sealed record BadgeScanned(string LocationId, DateTimeOffset From) : IEvent;
