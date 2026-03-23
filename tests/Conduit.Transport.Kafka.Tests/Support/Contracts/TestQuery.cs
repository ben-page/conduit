using Conduit.Messages;

namespace Conduit.Transport.Kafka.Tests.Support.Contracts;

public record TestQuery(string Input) : IQuery<TestQueryResult>;
