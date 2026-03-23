using Conduit.Messages;

namespace Conduit.Transport.Kafka.Tests.Support.Contracts;

public record TestVoidCommand(string Input) : ICommand;
