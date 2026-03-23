using Conduit.Messages;

namespace Conduit.Transport.RabbitMQ.Tests.Support.Contracts;

public record ThrowingQuery(string Input) : IQuery<TestQueryResult>;
