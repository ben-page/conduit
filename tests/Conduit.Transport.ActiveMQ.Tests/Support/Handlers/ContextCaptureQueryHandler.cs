using Conduit.Handlers;
using Conduit.Runtime;
using Conduit.Transport.ActiveMQ.Tests.Support.Contracts;

using Conduit.TestUtils;
namespace Conduit.Transport.ActiveMQ.Tests.Support.Handlers;

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
