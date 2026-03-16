using Conduit.Messages;

namespace Conduit.Tests.Support.ContextChain;

public record NestedEventOne(string Value) : IEvent;
