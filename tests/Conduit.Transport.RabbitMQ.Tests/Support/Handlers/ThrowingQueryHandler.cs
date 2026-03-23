using Conduit.Exceptions;
using Conduit.Handlers;
using Conduit.Transport.RabbitMQ.Tests.Support.Contracts;

namespace Conduit.Transport.RabbitMQ.Tests.Support.Handlers;

public class ThrowingQueryHandler : IQueryHandler<ThrowingQuery, TestQueryResult>
{
    public Task<TestQueryResult> HandleAsync(ThrowingQuery query, CancellationToken cancellationToken)
    {
        throw new NotFoundException($"Item '{query.Input}' not found");
    }
}
