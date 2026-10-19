using System.Runtime.ExceptionServices;

namespace Conduit.Runtime;

/// <summary>
/// One attempt of a top-level dispatch run under the host's <see cref="IDispatchRetryPolicy"/>, each in its own DI
/// scope. <see cref="Current"/> is set for the attempt's duration and flows with it (an <see cref="AsyncLocal{T}"/>),
/// so a dispatch from the attempt's own scope sees it and runs inline, while a dispatch from any other provider is
/// independent and gets attempts of its own.
/// </summary>
public sealed class DispatchAttempt
{
    private static readonly AsyncLocal<DispatchAttempt?> CurrentAttempt = new();

    private readonly Lock gate = new();
    private List<Action>? keptWork;
    private Decision decision;

    internal DispatchAttempt(int number, int maxAttempts, IServiceProvider services)
    {
        this.Number = number;
        this.MaxAttempts = maxAttempts;
        this.Services = services;
    }

    private enum Decision
    {
        Undecided,
        Kept,
        Abandoned,
    }

    /// <summary>The attempt running in this flow, or null outside one.</summary>
    public static DispatchAttempt? Current => CurrentAttempt.Value;

    /// <summary>This attempt's number, 1-based.</summary>
    public int Number { get; }

    /// <summary>The most attempts the dispatch gets.</summary>
    public int MaxAttempts { get; }

    /// <summary>
    /// The attempt scope's provider: a dispatch from it is nested in this attempt. Null once the scope is disposed, so a
    /// <see cref="Current"/> that flowed into work outliving the attempt pins nothing (and matches no live provider).
    /// </summary>
    internal IServiceProvider? Services { get; private set; }

    /// <summary>
    /// Runs <paramref name="action"/> once this attempt is known to be kept: it succeeded, or its failure is the
    /// dispatch's answer (no reason to retry, or no attempts left). Queued while the attempt is undecided; called after
    /// the decision, it runs at once on the caller's thread if the attempt was kept, and is dropped if it was abandoned
    /// for a retry. Thread-safe. The work should not throw: an exception from it reaches whoever made the decision
    /// (the dispatch's caller, in place of the dispatch's result or its own exception) or, after it, whoever called
    /// this. It is never offered to the policy.
    /// </summary>
    public void OnKept(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);

        lock (this.gate)
        {
            if (this.decision == Decision.Undecided)
            {
                (this.keptWork ??= []).Add(action);
                return;
            }

            if (this.decision == Decision.Abandoned)
            {
                return;
            }
        }

        action();
    }

    internal static void SetCurrent(DispatchAttempt? attempt)
    {
        CurrentAttempt.Value = attempt;
    }

    /// <summary>
    /// Decides the attempt kept and runs the work queued on it, in order, outside the lock. A no-op once the attempt is
    /// decided: the first decision is final. Every action runs even when one throws; then the one failure is rethrown
    /// as itself, or several as an <see cref="AggregateException"/>.
    /// </summary>
    internal void Keep()
    {
        List<Action>? work;
        lock (this.gate)
        {
            if (this.decision != Decision.Undecided)
            {
                return;
            }

            this.decision = Decision.Kept;
            work = this.keptWork;
            this.keptWork = null;
        }

        if (work is null)
        {
            return;
        }

        List<Exception>? failures = null;
        foreach (var action in work)
        {
            try
            {
                action();
            }
#pragma warning disable CA1031 // Not swallowed: rethrown once every action has run.
            catch (Exception exception)
#pragma warning restore CA1031
            {
                (failures ??= []).Add(exception);
            }
        }

        if (failures is null)
        {
            return;
        }

        if (failures.Count == 1)
        {
            ExceptionDispatchInfo.Throw(failures[0]);
        }

        throw new AggregateException(failures);
    }

    /// <summary>
    /// Decides the attempt abandoned for a retry and drops the work queued on it. A no-op once the attempt is decided.
    /// </summary>
    internal void Abandon()
    {
        lock (this.gate)
        {
            if (this.decision != Decision.Undecided)
            {
                return;
            }

            this.decision = Decision.Abandoned;
            this.keptWork = null;
        }
    }

    /// <summary>Drops the reference to the attempt scope's provider, once the runner has disposed the scope.</summary>
    internal void ReleaseServices()
    {
        this.Services = null;
    }
}
