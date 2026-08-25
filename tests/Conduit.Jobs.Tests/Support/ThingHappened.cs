using Conduit.Messages;

namespace Conduit.Jobs.Tests.Support;

/// <summary>The event the trigger tests publish.</summary>
public sealed record ThingHappened(string Label) : IEvent;
