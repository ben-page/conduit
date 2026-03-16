using Conduit.Messages;

namespace Conduit.Tests.Support.ContextChain;

public record NestedEventTwo(string Value) : IEvent;
