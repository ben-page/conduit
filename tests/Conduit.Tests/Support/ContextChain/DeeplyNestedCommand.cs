using Conduit.Messages;

namespace Conduit.Tests.Support.ContextChain;

public record DeeplyNestedCommand(string Value) : ICommand<string>;
