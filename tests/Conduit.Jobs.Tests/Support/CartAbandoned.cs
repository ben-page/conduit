using Conduit.Messages;

namespace Conduit.Jobs.Tests.Support;

/// <summary>The event the delayed-trigger tests publish.</summary>
public sealed record CartAbandoned(string CartId) : IEvent;
