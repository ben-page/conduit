using System.ComponentModel;
using Conduit.Runtime;
using Microsoft.Extensions.Configuration;

namespace Conduit.Configuration;

/// <summary>
/// Binds a transport's options from its endpoint configuration section.
/// Called by each transport package's <c>Use*</c> extension before the code callback runs,
/// so values set in code override configured ones. Infrastructure for transport packages; not intended for direct use.
/// </summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public static class ConduitConfigurationBinder
{
    /// <summary>
    /// Binds <paramref name="section"/> onto <paramref name="options"/>, including named
    /// receiver pools.
    /// </summary>
    /// <param name="options">The transport options to populate.</param>
    /// <param name="section">
    /// The endpoint's configuration section, or null when no configuration was supplied.
    /// </param>
    /// <param name="excludedKeys">
    /// Keys this transport binds by hand — because the binder cannot construct their type.
    /// A key excludes its whole subtree.
    /// </param>
    public static void BindTransportOptions(
        BrokerTransportOptions options,
        IConfiguration? section,
        params string[] excludedKeys)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (section is null || !section.GetChildren().Any())
        {
            return;
        }

        var poolSections = section.GetSection("Pools").GetChildren().ToList();
        ValidatePoolNames(poolSections);

        // The binder cannot construct some third-party types and throws when it tries
        // (AWSCredentials is abstract), so excluded keys are projected out before binding.
        var retained = section
            .AsEnumerable(makePathsRelative: true)
            .Where(entry => entry.Value is not null && !IsExcluded(entry.Key, excludedKeys))
            .ToList();

        new ConfigurationBuilder()
            .AddInMemoryCollection(retained)
            .Build()
            .Bind(options);

        // ReceiverPoolOptions.MessageTypes is an IReadOnlyList with no setter, which the binder
        // skips silently — leaving pools that consume concurrency but route nothing. Add by hand.
        foreach (var poolSection in poolSections)
        {
            var poolName = poolSection["Name"];
            var pool = options.Pools.FirstOrDefault(candidate => string.Equals(candidate.Name, poolName, StringComparison.OrdinalIgnoreCase));

            if (pool is null)
            {
                throw new InvalidOperationException(
                    $"Receiver pool '{poolName}' at '{poolSection.Path}' was not found after binding. " +
                    "Pool-related keys ('Pools', 'Name') must not be passed as excludedKeys to BindTransportOptions.");
            }

            foreach (var messageType in poolSection.GetSection("MessageTypes").GetChildren())
            {
                if (messageType.Value is { Length: > 0 } value)
                {
                    pool.Handle(value);
                }
            }
        }
    }

    private static void ValidatePoolNames(List<IConfigurationSection> poolSections)
    {
        var seenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var poolSection in poolSections)
        {
            var name = poolSection["Name"];

            // A missing required member binds as the empty string rather than throwing,
            // so an unnamed pool would otherwise fail silently.
            if (string.IsNullOrWhiteSpace(name))
            {
                throw new InvalidOperationException(
                    $"Receiver pool at '{poolSection.Path}' has no Name. Every pool must set a unique Name.");
            }

            // The binder fills Pools directly, never through AddPool, so its reservation is checked here too
            if (BrokerTransportOptions.IsReservedPoolName(name))
            {
                throw new InvalidOperationException(
                    $"Receiver pool name '{name}' at '{poolSection.Path}' is reserved: its queue would be named " +
                    "like a transport's own reply or dead-letter queue.");
            }

            if (!seenNames.Add(name))
            {
                throw new InvalidOperationException(
                    $"Receiver pool name '{name}' at '{poolSection.Path}' is declared more than once.");
            }
        }
    }

    private static bool IsExcluded(string key, string[] excludedKeys) =>
        excludedKeys.Any(excluded =>
            key.Equals(excluded, StringComparison.OrdinalIgnoreCase) ||
            key.StartsWith(excluded + ":", StringComparison.OrdinalIgnoreCase));
}
