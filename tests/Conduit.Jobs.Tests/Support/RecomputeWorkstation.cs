using Conduit.Messages;

namespace Conduit.Jobs.Tests.Support;

/// <summary>
/// The coalescing tests' job command. <c>From</c> is the window start, and merging keeps the earlier
/// of two — widening the window, never narrowing it.
/// </summary>
public sealed record RecomputeWorkstation(string LocationId, DateTimeOffset From) : ICommand;
