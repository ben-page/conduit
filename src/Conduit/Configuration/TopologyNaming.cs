using System.Globalization;
using System.IO.Hashing;
using System.Text;

namespace Conduit.Configuration;

public static class TopologyNaming
{
    public static string CreateSharedResourceName(ConduitOptions options, int maxLength)
    {
        ArgumentNullException.ThrowIfNull(options);
        var name = BuildBase(options.TopologyPrefix, options.SystemName);
        return Truncate(name, name, maxLength);
    }

    public static string CreateSharedResourceName(ConduitOptions options, string suffix, int maxLength)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(suffix);

        if (suffix.Contains('.', StringComparison.Ordinal))
        {
            var sanitized = SanitizeSuffix(suffix);
            return Truncate(sanitized, suffix, maxLength);
        }

        var name = $"{BuildBase(options.TopologyPrefix, options.SystemName)}-{suffix}";
        return Truncate(name, name, maxLength);
    }

    public static string CreateSharedResourceName<T>(ConduitOptions options, int maxLength)
    {
        var fullTypeName = typeof(T).FullName!;
        return CreateSharedResourceName(options, fullTypeName, maxLength);
    }

    public static string CreateAppResourceName(ConduitOptions options, int maxLength)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.ApplicationName, nameof(options.ApplicationName));
        var baseName = BuildBase(options.TopologyPrefix, options.SystemName);
        var name = $"{baseName}-{options.ApplicationName}";
        return Truncate(name, name, maxLength);
    }

    public static string CreateAppResourceName(ConduitOptions options, string suffix, int maxLength)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(suffix);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.ApplicationName, nameof(options.ApplicationName));

        if (suffix.Contains('.', StringComparison.Ordinal))
        {
            var sanitized = SanitizeSuffix(suffix);
            var name = $"{options.ApplicationName}-{sanitized}";
            return Truncate(name, suffix, maxLength);
        }

        var baseName = BuildBase(options.TopologyPrefix, options.SystemName);
        var plainName = $"{baseName}-{options.ApplicationName}-{suffix}";
        return Truncate(plainName, plainName, maxLength);
    }

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
