using Microsoft.Extensions.Configuration;

namespace Conduit.TestUtils;

/// <summary>
/// Loads test configuration from appsettings.json, user secrets, and environment variables.
/// Provides a consistent pattern for all transport test projects to resolve
/// connection strings and emulator preferences.
/// </summary>
/// <remarks>
/// Configuration priority (highest wins):
/// 1. Environment variables (e.g., Conduit__AzureServiceBus__ConnectionString)
/// 2. User secrets (per-project, via dotnet user-secrets)
/// 3. appsettings.json (checked-in defaults)
///
/// When a connection string is provided, the transport uses it directly —
/// no container is started. When no connection string is set and UseEmulator
/// is true (default), a Testcontainers container is started automatically.
/// </remarks>
public sealed class TestConfiguration
{
    private readonly IConfiguration configuration;

    private TestConfiguration(IConfiguration configuration)
    {
        this.configuration = configuration;
    }

    /// <summary>
    /// Builds a test configuration for the given test class type.
    /// The type is used to locate the user secrets ID from the assembly.
    /// </summary>
    public static TestConfiguration Load<TTestBase>() where TTestBase : class
    {
        var builder = new ConfigurationBuilder()
            .AddJsonFile("appsettings.json", optional: true);

        try
        {
            builder.AddUserSecrets<TTestBase>();
        }
        catch
        {
            // User secrets not configured for this project — that's fine
        }

        builder.AddEnvironmentVariables();

        return new TestConfiguration(builder.Build());
    }

    /// <summary>
    /// Gets the connection string for a transport, or null if not configured.
    /// </summary>
    /// <param name="transportName">The transport section name (e.g., "AzureServiceBus", "RabbitMQ").</param>
    public string? GetConnectionString(string transportName)
    {
        return this.configuration[$"Conduit:{transportName}:ConnectionString"];
    }

    /// <summary>
    /// Gets whether a Testcontainers broker should be started when no connection string
    /// is provided. Defaults to true. Set to false to skip container startup (tests
    /// will be marked Inconclusive if no connection string is configured).
    /// </summary>
    /// <param name="transportName">The transport section name (e.g., "AzureServiceBus", "RabbitMQ").</param>
    public bool UseTestContainer(string transportName)
    {
        var value = this.configuration[$"Conduit:{transportName}:UseTestContainer"];

        if (string.IsNullOrWhiteSpace(value))
        {
            return true;
        }

        return bool.TryParse(value, out var result) && result;
    }

    /// <summary>
    /// Gets a named configuration value for a transport.
    /// </summary>
    /// <param name="transportName">The transport section name.</param>
    /// <param name="key">The configuration key within the transport section.</param>
    public string? Get(string transportName, string key)
    {
        return this.configuration[$"Conduit:{transportName}:{key}"];
    }
}
