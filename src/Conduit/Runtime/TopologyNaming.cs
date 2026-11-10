using System.Globalization;
using System.IO.Hashing;
using System.Text;
using Conduit.Configuration;

namespace Conduit.Runtime;

/// <summary>
/// Generates consistent resource names for broker transport topology.
/// Handles prefix/system-name composition, sanitizing to the characters every broker accepts, and
/// hash-based truncation.
/// </summary>
/// <remarks>
/// Every name is composed, then sanitized (<c>[A-Za-z0-9_-]</c> only, anything else becomes <c>-</c>: a
/// Windows <c>DOMAIN\user</c> prefix, a dotted application name, a nested type's <c>+</c>), then truncated
/// when longer than the broker allows: the first <c>maxLength - 9</c> characters, <c>-</c>, and 8 hex
/// characters of an xxHash64 of the whole sanitized name, so two names that differ only past the cut stay
/// apart. The names are part of a deployment's topology: changing how they are built renames queues.
/// </remarks>
public static class TopologyNaming
{
    // "-" plus 8 hex characters
    private const int HashSuffixLength = 9;

    /// <summary>
    /// Creates a shared resource name from the system name and optional topology prefix.
    /// Shared names are identical across all application instances (used for unicast resources).
    /// </summary>
    /// <param name="options">Conduit options providing system name and topology prefix.</param>
    /// <param name="maxLength">Maximum allowed length. Names exceeding this are hash-truncated.</param>
    public static string CreateSharedResourceName(ConduitOptions options, int maxLength)
    {
        ArgumentNullException.ThrowIfNull(options);
        return Finish(BuildBase(options.TopologyPrefix, options.SystemName), maxLength);
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

        var baseName = BuildBase(options.TopologyPrefix, options.SystemName);
        return Finish($"{baseName}-{ReverseSegments(suffix)}", maxLength);
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
        return Finish($"{baseName}-{options.ApplicationName}", maxLength);
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
        return Finish($"{baseName}-{options.ApplicationName}-{ReverseSegments(suffix)}", maxLength);
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

    /// <summary>
    /// Replaces every character outside <c>[A-Za-z0-9_-]</c>, the set every broker accepts in a resource
    /// name, with <c>-</c>, one for one.
    /// </summary>
    internal static string Sanitize(string name) =>
        string.Create(name.Length, name, static (sanitized, source) =>
        {
            for (var index = 0; index < source.Length; index++)
            {
                var character = source[index];
                sanitized[index] = char.IsAsciiLetterOrDigit(character) || character is '_' or '-'
                    ? character
                    : '-';
            }
        });

    private static string BuildBase(string? prefix, string systemName) =>
        prefix is not null ? $"{prefix}-{systemName}" : systemName;

    private static string ReverseSegments(string suffix)
    {
        if (!suffix.Contains('.', StringComparison.Ordinal))
        {
            return suffix;
        }

        var segments = suffix.Split('.');
        Array.Reverse(segments);
        return string.Join('-', segments);
    }

    private static string Finish(string name, int maxLength)
    {
        var sanitized = Sanitize(name);
        if (sanitized.Length <= maxLength)
        {
            return sanitized;
        }

        // The hash covers the whole name, so siblings that share everything the cut keeps (two long
        // application names, one message type) still differ
        var hash = XxHash64.HashToUInt64(Encoding.UTF8.GetBytes(sanitized));
        var hashHex = hash.ToString("x16", CultureInfo.InvariantCulture)[..(HashSuffixLength - 1)];
        return $"{sanitized[..(maxLength - HashSuffixLength)]}-{hashHex}";
    }
}
