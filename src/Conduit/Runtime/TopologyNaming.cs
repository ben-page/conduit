using System.Globalization;
using System.IO.Hashing;
using System.Text;
using Conduit.Configuration;

namespace Conduit.Runtime;

/// <summary>
/// Generates consistent resource names for broker transport topology.
/// Handles prefix/system-name composition, suffix sanitization, and hash-based truncation.
/// </summary>
public static class TopologyNaming
{
    /// <summary>
    /// Creates a shared resource name from the system name and optional topology prefix.
    /// Shared names are identical across all application instances (used for unicast resources).
    /// </summary>
    /// <param name="options">Conduit options providing system name and topology prefix.</param>
    /// <param name="maxLength">Maximum allowed length. Names exceeding this are hash-truncated.</param>
    public static string CreateSharedResourceName(ConduitOptions options, int maxLength)
    {
        ArgumentNullException.ThrowIfNull(options);
        var name = BuildBase(options.TopologyPrefix, options.SystemName);
        return Truncate(name, name, maxLength);
    }

    /// <summary>
    /// Creates a shared resource name with a suffix (typically a message type name).
    /// Dotted suffixes are reversed and hyphenated (e.g. "Orders.Commands.Create" becomes "Create-Commands-Orders").
    /// </summary>
    /// <param name="options">Conduit options providing system name and topology prefix.</param>
    /// <param name="suffix">The suffix to append (e.g. a full type name).</param>
    /// <param name="maxLength">Maximum allowed length. Names exceeding this are hash-truncated.</param>
    public static string CreateSharedResourceName(ConduitOptions options, string suffix, int maxLength)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(suffix);

        if (suffix.Contains('.', StringComparison.Ordinal))
        {
            var baseName = BuildBase(options.TopologyPrefix, options.SystemName);
            var sanitized = SanitizeSuffix(suffix);
            var name = $"{baseName}-{sanitized}";
            return Truncate(name, suffix, maxLength);
        }

        var fullName = $"{BuildBase(options.TopologyPrefix, options.SystemName)}-{suffix}";
        return Truncate(fullName, fullName, maxLength);
    }

    /// <summary>
    /// Creates a shared resource name using <typeparamref name="T"/>'s full type name as the suffix.
    /// </summary>
    /// <typeparam name="T">The message type whose full name is used as the suffix.</typeparam>
    /// <param name="options">Conduit options providing system name and topology prefix.</param>
    /// <param name="maxLength">Maximum allowed length. Names exceeding this are hash-truncated.</param>
    public static string CreateSharedResourceName<T>(ConduitOptions options, int maxLength)
    {
        var fullTypeName = typeof(T).FullName!;
        return CreateSharedResourceName(options, fullTypeName, maxLength);
    }

    /// <summary>
    /// Creates an app-scoped resource name including the application name.
    /// App-scoped names are unique per application and shared by all instances of it, which makes
    /// the resulting queue a competing-consumer queue (used for multicast resources).
    /// </summary>
    /// <param name="options">Conduit options providing system name, application name, and topology prefix.</param>
    /// <param name="maxLength">Maximum allowed length. Names exceeding this are hash-truncated.</param>
    public static string CreateAppResourceName(ConduitOptions options, int maxLength)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.ApplicationName, nameof(options.ApplicationName));
        var baseName = BuildBase(options.TopologyPrefix, options.SystemName);
        var name = $"{baseName}-{options.ApplicationName}";
        return Truncate(name, name, maxLength);
    }

    /// <summary>
    /// Creates an app-scoped resource name with a suffix (typically a message type name).
    /// Dotted suffixes are reversed and hyphenated.
    /// </summary>
    /// <param name="options">Conduit options providing system name, application name, and topology prefix.</param>
    /// <param name="suffix">The suffix to append (e.g. a full type name).</param>
    /// <param name="maxLength">Maximum allowed length. Names exceeding this are hash-truncated.</param>
    public static string CreateAppResourceName(ConduitOptions options, string suffix, int maxLength)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(suffix);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.ApplicationName, nameof(options.ApplicationName));

        var baseName = BuildBase(options.TopologyPrefix, options.SystemName);

        if (suffix.Contains('.', StringComparison.Ordinal))
        {
            var sanitized = SanitizeSuffix(suffix);
            var name = $"{baseName}-{options.ApplicationName}-{sanitized}";
            return Truncate(name, suffix, maxLength);
        }

        var plainName = $"{baseName}-{options.ApplicationName}-{suffix}";
        return Truncate(plainName, plainName, maxLength);
    }

    /// <summary>
    /// Creates an app-scoped resource name using <typeparamref name="T"/>'s full type name as the suffix.
    /// </summary>
    /// <typeparam name="T">The message type whose full name is used as the suffix.</typeparam>
    /// <param name="options">Conduit options providing system name, application name, and topology prefix.</param>
    /// <param name="maxLength">Maximum allowed length. Names exceeding this are hash-truncated.</param>
    public static string CreateAppResourceName<T>(ConduitOptions options, int maxLength)
    {
        var fullTypeName = typeof(T).FullName!;
        return CreateAppResourceName(options, fullTypeName, maxLength);
    }

    private static string BuildBase(string? prefix, string systemName) =>
        prefix is not null ? $"{prefix}-{systemName}" : systemName;

    private static string SanitizeSuffix(string suffix)
    {
        if (!suffix.Contains('.', StringComparison.Ordinal))
        {
            return suffix;
        }

        var segments = suffix.Split('.');
        Array.Reverse(segments);
        return string.Join('-', segments);
    }

    private static string Truncate(string name, string hashInput, int maxLength)
    {
        if (name.Length <= maxLength)
        {
            return name;
        }

        const int hashSuffixLength = 9;
        var hash = XxHash32.HashToUInt32(Encoding.UTF8.GetBytes(hashInput));
        var hashHex = hash.ToString("x8", CultureInfo.InvariantCulture);
        var truncated = name[..(maxLength - hashSuffixLength)];
        return $"{truncated}-{hashHex}";
    }
}
