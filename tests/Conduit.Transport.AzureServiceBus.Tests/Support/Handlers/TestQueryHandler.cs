using Conduit.Handlers;
using Conduit.Transport.AzureServiceBus.Tests.Support.Contracts;

using Conduit.TestUtils;
namespace Conduit.Transport.AzureServiceBus.Tests.Support.Handlers;

public class TestQueryHandler(SpyLog spyLog) : IQueryHandler<TestQuery, TestQueryResult>
{
    public Task<TestQueryResult> HandleAsync(TestQuery query, CancellationToken cancellationToken)
    {
        spyLog.Record(query);
        return Task.FromResult(new TestQueryResult($"result:{query.Input}"));
    }
}
