using System.Collections.Concurrent;
using Conduit.Runtime;

namespace Conduit.Tests.Support;

/// <summary>
/// An <see cref="IDispatchRetryPolicy"/> a test scripts after the provider is built (it is resolved as a singleton),
/// recording every failure it is asked about and, while that attempt's scope is open, whether its probe was disposed.
/// </summary>
public sealed class RecordingRetryPolicy : IDispatchRetryPolicy
{
    private readonly ConcurrentQueue<DispatchAttemptFailure> failures = new();
    private readonly ConcurrentQueue<(Guid ProbeId, bool ProbeDisposed)> probesSeen = new();

    public Func<Type, bool> CoversType { get; set; } = _ => true;

    public Func<DispatchAttemptFailure, string?> Reason { get; set; } = _ => "retry-me";

    public IReadOnlyList<DispatchAttemptFailure> Failures => [.. this.failures];

    public IReadOnlyList<(Guid ProbeId, bool ProbeDisposed)> ProbesSeen => [.. this.probesSeen];

    public bool Covers(Type requestType) => this.CoversType(requestType);

    public string? RetryReason(DispatchAttemptFailure failure)
    {
        this.failures.Enqueue(failure);
        if (failure.AttemptServices.GetService(typeof(AttemptProbe)) is AttemptProbe probe)
        {
            this.probesSeen.Enqueue((probe.Id, probe.Disposed));
        }

        return this.Reason(failure);
    }
}
