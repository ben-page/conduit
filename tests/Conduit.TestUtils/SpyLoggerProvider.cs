using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace Conduit.TestUtils;

/// <summary>
/// An <see cref="ILoggerProvider"/> that records every entry of every category, for asserting on what a host logged:
/// <c>builder.Logging.AddProvider(spy)</c>. The host's minimum level still applies.
/// </summary>
public sealed class SpyLoggerProvider : ILoggerProvider
{
    private readonly ConcurrentQueue<SpyLogEntry> entries = new();

    /// <summary>Every entry logged so far, in order.</summary>
    public IReadOnlyList<SpyLogEntry> Entries => [.. this.entries];

    /// <inheritdoc/>
    public ILogger CreateLogger(string categoryName) => new SpyLogger(categoryName, this.entries);

    /// <inheritdoc/>
    public void Dispose()
    {
    }

    private sealed class SpyLogger(string category, ConcurrentQueue<SpyLogEntry> entries) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull
            => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            ArgumentNullException.ThrowIfNull(formatter);
            entries.Enqueue(new SpyLogEntry(category, logLevel, eventId, formatter(state, exception), exception));
        }
    }
}
