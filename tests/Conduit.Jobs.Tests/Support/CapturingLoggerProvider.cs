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
