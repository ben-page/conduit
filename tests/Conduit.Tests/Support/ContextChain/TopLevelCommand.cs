using Conduit.Messages;

namespace Conduit.Tests.Support.ContextChain;

public record TopLevelCommand(string Value) : ICommand<string>;
