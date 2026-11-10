using Conduit.Messages;

namespace Conduit.Tests.Support;

/// <summary>An event whose only listener, <see cref="UnbuildableListener"/>, cannot be built.</summary>
public sealed record UnbuildableListenerEvent(string Data) : IEvent;
