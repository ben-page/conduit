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
    private int remaining;

    /// <summary>Creates a fault for one store member, disarmed.</summary>
    /// <param name="memberName">The <see cref="IJobStore"/> member whose next call throws once armed.</param>
    public JobStoreFault(string memberName) => this.MemberName = memberName;

    /// <summary>The <see cref="IJobStore"/> member that throws.</summary>
    public string MemberName { get; }

    /// <summary>Arms the fault: the next call of <see cref="MemberName"/> throws.</summary>
    public void Arm() => Volatile.Write(ref this.remaining, 1);

    /// <summary>Consumes the fault when <paramref name="memberName"/> is the armed member.</summary>
    /// <param name="memberName">The member being called.</param>
    /// <returns><see langword="true"/> when this call must throw.</returns>
    public bool TryTake(string memberName)
        => memberName == this.MemberName && Interlocked.CompareExchange(ref this.remaining, 0, 1) == 1;
}
