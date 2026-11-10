namespace Conduit.Jobs.Tests.Support;

/// <summary>
/// A one-shot action for <see cref="HookedJobStore"/> to run just before the next call of one
/// <see cref="Persistence.IJobStore"/> member, once armed: a way to land something (a clock step, say) at an exact
/// point inside the runner's sequence of store calls. Held per test, so parallel tests cannot take each other's.
/// </summary>
public sealed class JobStoreHook
{
    private readonly Action action;
    private int armed;

    /// <summary>Creates a hook for one store member, disarmed.</summary>
    /// <param name="memberName">The member whose next call runs <paramref name="action"/> once armed.</param>
    /// <param name="action">What to run before that call reaches the store.</param>
    public JobStoreHook(string memberName, Action action)
    {
        this.MemberName = memberName;
        this.action = action;
    }

    /// <summary>The member the hook watches.</summary>
    public string MemberName { get; }

    /// <summary>Arms the hook: the next call of <see cref="MemberName"/> runs the action first.</summary>
    public void Arm() => Volatile.Write(ref this.armed, 1);

    /// <summary>Runs the action if <paramref name="memberName"/> is the armed member, disarming it.</summary>
    public void RunIfArmed(string memberName)
    {
        if (memberName == this.MemberName && Interlocked.CompareExchange(ref this.armed, 0, 1) == 1)
        {
            this.action();
        }
    }
}
