using Conduit.Messages;

namespace Conduit.Audit.Tests.Support;

/// <summary>A message no audit registration suppresses: the control for the suppression tests.</summary>
public sealed record CapturedEvent(string Note) : IEvent;
