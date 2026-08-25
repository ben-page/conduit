namespace Conduit.Jobs;

/// <summary>Narrows a run query.</summary>
/// <param name="ScheduleName">Restricts results to one schedule or trigger when supplied.</param>
public sealed record JobFilter(string? ScheduleName = null)
{
    /// <summary>Matches every run.</summary>
    public static JobFilter All { get; } = new();
}
