using Conduit.Configuration;
using Conduit.Generated;
using Conduit.Jobs.Configuration;
using Conduit.Jobs.Runtime;
using Conduit.Persistence.Sqlite;
using Conduit.TestUtils;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;

namespace Conduit.Jobs.Tests.Support;

/// <summary>
/// Builds and starts a host with the jobs subsystem configured. Several hosts in one test stand in
/// for the replicas of one application, so they share the caller's <see cref="JobSpy"/>,
/// <see cref="FakeTimeProvider"/> and connection string.
/// </summary>
public static class JobsTestHost
{
    /// <summary>
    /// Starts a host and waits until its scheduling loops are actually waiting on the clock.
    /// Supplying a connection string makes the jobs durable.
    /// </summary>
    public static async Task<TestHost> StartAsync(
        Action<JobsBuilder> configureJobs,
        JobSpy? spy = null,
        TimeProvider? timeProvider = null,
        string? connectionString = null,
        CapturingLoggerProvider? logs = null,
        LocationSummarySpy? locationSummarySpy = null,
        Action<IServiceCollection>? configureServices = null,
        Action<ConduitBuilder>? configureConduit = null)
    {
        ArgumentNullException.ThrowIfNull(configureJobs);

        var resolvedSpy = spy ?? new JobSpy();
        var resolvedTime = timeProvider ?? TimeProvider.System;

        var host = await TestHost.StartAsync(builder =>
        {
            builder.Logging.ClearProviders();
            if (logs is not null)
            {
                builder.Logging.AddProvider(logs);
            }

            // Registered before AddConduit so the subsystem's TryAddSingleton(TimeProvider.System)
            // does not win: nothing in the scheduler may read the ambient clock.
            builder.Services.AddSingleton(resolvedTime);
            builder.Services.AddSingleton(resolvedSpy);
            builder.Services.AddSingleton(locationSummarySpy ?? new LocationSummarySpy());

            builder.Services.AddConduit(conduit =>
            {
                configureConduit?.Invoke(conduit);

                // The engine must be registered before AddJobs — that is where durability is decided.
                if (connectionString is not null)
                {
                    conduit.UseSqlite(connectionString);
                }

                conduit.AddJobs(configureJobs);
            });

            // After AddConduit, so a test can replace what the subsystem registered.
            configureServices?.Invoke(builder.Services);
        });

        // Without this the test could advance a FakeTimeProvider past an occurrence before any loop
        // had armed a timer for it, and the host would sit waiting for the occurrence after.
        await host.GetRequiredService<JobSchedulerReadiness>().Ready
            .WaitAsync(TimeSpan.FromSeconds(10));

        return host;
    }
}
