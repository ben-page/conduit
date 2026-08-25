using Conduit.Generated;
using Conduit.Jobs.Configuration;
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
    /// <summary>Starts a host. Supplying a connection string makes the jobs durable.</summary>
    public static Task<TestHost> StartAsync(
        Action<JobsBuilder> configureJobs,
        JobSpy? spy = null,
        TimeProvider? timeProvider = null,
        string? connectionString = null,
        CapturingLoggerProvider? logs = null)
    {
        ArgumentNullException.ThrowIfNull(configureJobs);

        var resolvedSpy = spy ?? new JobSpy();
        var resolvedTime = timeProvider ?? TimeProvider.System;

        return TestHost.StartAsync(builder =>
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

            builder.Services.AddConduit(conduit =>
            {
                // The engine must be registered before AddJobs — that is where durability is decided.
                if (connectionString is not null)
                {
                    conduit.UseSqlite(connectionString);
                }

                conduit.AddJobs(configureJobs);
            });
        });
    }
}
