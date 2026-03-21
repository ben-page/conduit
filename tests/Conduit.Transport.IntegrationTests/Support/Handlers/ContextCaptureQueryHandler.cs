using Conduit.Handlers;
using Conduit.Runtime;
using Conduit.Transport.IntegrationTests.Support.Contracts;

namespace Conduit.Transport.IntegrationTests.Support.Handlers;

public class ContextCaptureQueryHandler(SpyLog spyLog)
    : IQueryHandler<ContextCaptureQuery, TestQueryResult>
{
    public Task<TestQueryResult> HandleAsync(
        ContextCaptureQuery query, CancellationToken cancellationToken)
    {
        var ctx = MessageContext.Current;
        var snapshot = new MessageContextSnapshot(ctx?.MessageId, ctx?.CausationId, ctx?.ConversationId);
        spyLog.Record(snapshot);
        return Task.FromResult(new TestQueryResult($"captured:{query.Input}"));
    }
}
