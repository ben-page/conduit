using Conduit.Messages;

namespace Conduit.Tests.Support;

public record EchoCommand(string Value) : ICommand<string>;
