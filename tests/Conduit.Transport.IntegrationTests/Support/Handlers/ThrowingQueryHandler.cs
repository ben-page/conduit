using Conduit.Exceptions;
using Conduit.Handlers;
using Conduit.Transport.IntegrationTests.Support.Contracts;

namespace Conduit.Transport.IntegrationTests.Support.Handlers;

public class ThrowingQueryHandler : IQueryHandler<ThrowingQuery, TestQueryResult>
{
    public Task<TestQueryResult> HandleAsync(ThrowingQuery query, CancellationToken cancellationToken)
    {
        throw new NotFoundException($"Item '{query.Input}' not found");
    }
}
