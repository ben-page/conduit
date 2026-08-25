namespace Conduit.Jobs.Configuration;

/// <summary>What to do when occurrences were missed while the application was not running.</summary>
public enum MisfirePolicy
{
    /// <summary>Advance past every missed occurrence without running anything. The default.</summary>
    Skip = 0,

    /// <summary>Run exactly one catch-up occurrence, then resume the normal schedule.</summary>
    RunImmediately = 1,
}
