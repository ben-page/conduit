using Conduit.Attributes;
using Conduit.Messages;

namespace Conduit.Transport.RabbitMQ.Tests.Support.Contracts;

[FireAndForget]
public record ThrowingFireAndForgetCommand(string Input) : ICommand;
