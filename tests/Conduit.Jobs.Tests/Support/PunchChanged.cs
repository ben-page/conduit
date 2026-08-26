using Conduit.Messages;

namespace Conduit.Jobs.Tests.Support;

/// <summary>The event the coalescing tests publish, standing in for DMS's PunchChangedEvent.</summary>
public sealed record PunchChanged(string LocationId, DateTimeOffset From) : IEvent;
