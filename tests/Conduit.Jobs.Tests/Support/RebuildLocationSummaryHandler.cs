using Conduit.Handlers;

namespace Conduit.Jobs.Tests.Support;

/// <summary>Records the scope the rebuild ran with, so tests can assert the merge widened it.</summary>
public sealed class RebuildLocationSummaryHandler : ICommandHandler<RebuildLocationSummary>
{
    private readonly LocationSummarySpy spy;

    public RebuildLocationSummaryHandler(LocationSummarySpy spy)
    {
        this.spy = spy;
    }

    public Task HandleAsync(RebuildLocationSummary command, CancellationToken cancellationToken)
        => this.spy.RecordAsync(command, cancellationToken);
}
