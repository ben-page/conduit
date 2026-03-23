using Conduit.Messages;

namespace Conduit.Transport.RabbitMQ.Tests.Support.Contracts;

public record TestQuery(string Input) : IQuery<TestQueryResult>;
