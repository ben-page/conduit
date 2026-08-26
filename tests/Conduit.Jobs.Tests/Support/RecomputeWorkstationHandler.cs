using Conduit.Handlers;

namespace Conduit.Jobs.Tests.Support;

/// <summary>Records the scope the rebuild ran with, so tests can assert the merge widened it.</summary>
public sealed class RecomputeWorkstationHandler : ICommandHandler<RecomputeWorkstation>
{
    private readonly WorkstationSpy spy;

    public RecomputeWorkstationHandler(WorkstationSpy spy)
    {
        this.spy = spy;
    }

    public Task HandleAsync(RecomputeWorkstation command, CancellationToken cancellationToken)
        => this.spy.RecordAsync(command, cancellationToken);
}
