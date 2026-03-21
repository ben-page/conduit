using Conduit.Messages;

namespace Conduit.Transport.IntegrationTests.Support.Contracts;

public record TestQuery(string Input) : IQuery<TestQueryResult>;
