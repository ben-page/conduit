using Conduit.Jobs.Persistence;

namespace Conduit.Jobs.Tests.Support;

/// <summary>
/// One database error for <see cref="FaultingJobStore"/> to raise: once armed, the next call of the
/// named <see cref="IJobStore"/> member, on any store the proxy wraps for this host, throws instead of
/// reaching the database. Held per test rather than statically, so tests running in parallel cannot
/// take each other's fault.
/// </summary>
public sealed class JobStoreFault
{
    private readonly TaskCompletionSource taken = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int remaining;

    /// <summary>Creates a fault for one store member, disarmed.</summary>
    /// <param name="memberName">The <see cref="IJobStore"/> member whose next call throws once armed.</param>
    public JobStoreFault(string memberName) => this.MemberName = memberName;

    /// <summary>The <see cref="IJobStore"/> member that throws.</summary>
    public string MemberName { get; }

    /// <summary>
    /// When set, the armed call does not throw at once: it waits for its own cancellation token and
    /// then throws an exception that is not cancellation — what SqlClient does with a command a
    /// stopping host cancels. The member must return <see cref="Task{TResult}"/>.
    /// </summary>
    public bool ThrowsOnceCancelled { get; init; }

    /// <summary>Completes when the armed call has been made.</summary>
    public Task Taken => this.taken.Task;

    /// <summary>Arms the fault: the next call of <see cref="MemberName"/> throws.</summary>
    public void Arm() => Volatile.Write(ref this.remaining, 1);

    /// <summary>Consumes the fault when <paramref name="memberName"/> is the armed member.</summary>
    /// <param name="memberName">The member being called.</param>
    /// <returns><see langword="true"/> when this call must throw.</returns>
    public bool TryTake(string memberName)
    {
        if (memberName != this.MemberName || Interlocked.CompareExchange(ref this.remaining, 0, 1) != 1)
        {
            return false;
        }

        this.taken.TrySetResult();
        return true;
    }
}
