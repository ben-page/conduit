using System.Collections.Concurrent;
using System.Globalization;
using Microsoft.Extensions.Logging;

namespace Conduit.Jobs.Tests.Support;

/// <summary>Captures formatted log messages so tests can assert on startup diagnostics.</summary>
public sealed class CapturingLoggerProvider : ILoggerProvider
{
    private readonly ConcurrentQueue<string> warnings = new();

    /// <summary>Every message logged at Warning or above.</summary>
    public IReadOnlyList<string> Warnings => [.. this.warnings];

    /// <summary>
    /// Waits for a matching warning. Startup logging is not guaranteed to have happened by the time
    /// the host's StartAsync returns, so asserting on it directly is a race.
    /// </summary>
    public async Task<string> WaitForWarningAsync(Func<string, bool> match, TimeSpan? timeout = null)
    {
        ArgumentNullException.ThrowIfNull(match);

        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(5));
        while (true)
        {
            var found = this.Warnings.FirstOrDefault(match);
            if (found is not null)
            {
                return found;
            }

            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException(
                    $"No matching warning was logged. Saw: [{string.Join(" | ", this.Warnings)}]");
            }

            await Task.Delay(10);
        }
    }

    /// <inheritdoc/>
    public ILogger CreateLogger(string categoryName) => new CapturingLogger(this.warnings);

    /// <inheritdoc/>
    public void Dispose()
    {
    }

    private sealed class CapturingLogger : ILogger
    {
        private readonly ConcurrentQueue<string> warnings;

        public CapturingLogger(ConcurrentQueue<string> warnings) => this.warnings = warnings;

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull
            => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            ArgumentNullException.ThrowIfNull(formatter);

            if (logLevel >= LogLevel.Warning)
            {
                this.warnings.Enqueue(
                    string.Create(CultureInfo.InvariantCulture, $"{formatter(state, exception)}"));
            }
        }
    }
}
