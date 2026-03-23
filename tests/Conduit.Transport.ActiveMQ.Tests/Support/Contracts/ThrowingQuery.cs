using Conduit.Messages;

namespace Conduit.Transport.ActiveMQ.Tests.Support.Contracts;

public record ThrowingQuery(string Input) : IQuery<TestQueryResult>;
