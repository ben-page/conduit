using Conduit.Attributes;
using Conduit.Messages;

namespace Conduit.Transport.RabbitMQ.Tests.Support.Contracts;

[FireAndForget]
public record FireAndForgetCommand(string Input) : ICommand;
