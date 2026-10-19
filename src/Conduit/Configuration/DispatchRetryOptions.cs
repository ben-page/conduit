namespace Conduit.Configuration;

/// <summary>
/// How a dispatch covered by the host's <see cref="Conduit.Runtime.IDispatchRetryPolicy"/> is retried. Set through
/// <see cref="ConduitBuilder.AddDispatchRetry{TPolicy}"/>, which validates it.
/// </summary>
public sealed class DispatchRetryOptions
{
    /// <summary>The most attempts a dispatch gets, the first included. At least 1. Default 3.</summary>
    public int MaxAttempts { get; set; } = 3;

    /// <summary>
    /// The wait before each retry, drawn uniformly from a range: before attempt <c>n + 1</c> it is
    /// <c>Delays[min(n - 1, Count - 1)]</c>, so the last range repeats. Must not be empty when
    /// <see cref="MaxAttempts"/> is above 1. Default 10–100 ms, then 50–300 ms.
    /// </summary>
    public IList<DispatchRetryDelay> Delays { get; } =
    [
        new(TimeSpan.FromMilliseconds(10), TimeSpan.FromMilliseconds(100)),
        new(TimeSpan.FromMilliseconds(50), TimeSpan.FromMilliseconds(300)),
    ];

    /// <summary>
    /// The retry clock. Default <see cref="TimeProvider.System"/>, and deliberately never the host's DI
    /// <see cref="TimeProvider"/>: that is the business clock, and a test host that fakes it would never see a wait end.
    /// Only Conduit's own tests set this.
    /// </summary>
    public TimeProvider TimeProvider { get; set; } = TimeProvider.System;

    internal void Validate()
    {
        if (this.MaxAttempts < 1)
        {
            throw new InvalidOperationException(
                $"DispatchRetryOptions.MaxAttempts must be at least 1; it is {this.MaxAttempts}.");
        }

        if (this.MaxAttempts > 1 && this.Delays.Count == 0)
        {
            throw new InvalidOperationException(
                "DispatchRetryOptions.Delays is empty, but MaxAttempts allows a retry; add at least one delay range.");
        }

        for (var index = 0; index < this.Delays.Count; index++)
        {
            var delay = this.Delays[index];
            if (delay.Min < TimeSpan.Zero || delay.Min > delay.Max)
            {
                throw new InvalidOperationException(
                    $"DispatchRetryOptions.Delays[{index}] is {delay.Min}..{delay.Max}; " +
                    "Min must be at least zero and at most Max.");
            }
        }

        if (this.TimeProvider is null)
        {
            throw new InvalidOperationException("DispatchRetryOptions.TimeProvider must not be null.");
        }
    }
}

/// <summary>A range a retry's wait is drawn from, uniformly.</summary>
/// <param name="Min">The shortest wait, at least zero.</param>
/// <param name="Max">The longest wait, at least <paramref name="Min"/>.</param>
public readonly record struct DispatchRetryDelay(TimeSpan Min, TimeSpan Max);
