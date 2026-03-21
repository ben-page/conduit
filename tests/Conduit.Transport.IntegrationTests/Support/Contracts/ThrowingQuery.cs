using Conduit.Messages;

namespace Conduit.Transport.IntegrationTests.Support.Contracts;

public record ThrowingQuery(string Input) : IQuery<TestQueryResult>;
