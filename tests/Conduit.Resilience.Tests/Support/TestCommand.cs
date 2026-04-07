using Conduit.Messages;

namespace Conduit.Resilience.Tests.Support;

public record TestCommand(string Value) : ICommand<string>;
