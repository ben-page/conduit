using Microsoft.Extensions.Logging;

namespace Conduit.TestUtils;

/// <summary>One entry a <see cref="SpyLoggerProvider"/> recorded.</summary>
public sealed record SpyLogEntry(string Category, LogLevel Level, EventId EventId, string Message, Exception? Exception);
