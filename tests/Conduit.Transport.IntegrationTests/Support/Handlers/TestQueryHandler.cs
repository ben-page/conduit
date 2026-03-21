using Conduit.Handlers;
using Conduit.Transport.IntegrationTests.Support.Contracts;

namespace Conduit.Transport.IntegrationTests.Support.Handlers;

public class TestQueryHandler(SpyLog spyLog) : IQueryHandler<TestQuery, TestQueryResult>
{
    public Task<TestQueryResult> HandleAsync(TestQuery query, CancellationToken cancellationToken)
    {
        spyLog.Record(query);
        return Task.FromResult(new TestQueryResult($"result:{query.Input}"));
    }
}
