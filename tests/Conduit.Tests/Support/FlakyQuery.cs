using Conduit.Handlers;
using Conduit.Messages;
using Conduit.TestUtils;

namespace Conduit.Tests.Support;

/// <summary>A query whose handler throws <see cref="RetryMeException"/> on its first <paramref name="Failures"/> calls.</summary>
public record FlakyQuery(int Failures) : IQuery<Guid>;

/// <summary>Records a sighting, fails while calls remain, then returns the id of the scope it was resolved from.</summary>
public class FlakyQueryHandler(RetryProbe probe, AttemptProbe scope) : IQueryHandler<FlakyQuery, Guid>
{
    public Task<Guid> HandleAsync(FlakyQuery query, CancellationToken cancellationToken)
    {
        var call = probe.See(nameof(FlakyQuery), scope);
        if (call <= query.Failures)
        {
            throw new RetryMeException($"{nameof(FlakyQuery)} failed on call {call}");
        }

        return Task.FromResult(scope.Id);
    }
}
