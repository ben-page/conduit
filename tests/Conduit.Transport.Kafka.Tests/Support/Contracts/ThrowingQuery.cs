using Conduit.Messages;

namespace Conduit.Transport.Kafka.Tests.Support.Contracts;

public record ThrowingQuery(string Input) : IQuery<TestQueryResult>;
