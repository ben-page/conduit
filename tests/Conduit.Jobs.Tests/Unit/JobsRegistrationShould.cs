using AwesomeAssertions;
using Conduit.Configuration;
using Conduit.Handlers;
using Conduit.Jobs.Configuration;
using Conduit.Jobs.Persistence;
using Conduit.Jobs.Runtime;
using Conduit.Jobs.Tests.Support;
using Conduit.Persistence.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Conduit.Jobs.Tests.Unit;

/// <summary>
/// What <c>AddJobs</c> puts in the container, per mode. The runner is a set of hosted services and
/// singletons a manage-only host must never get, so the assertions name each of them rather than
/// counting.
/// </summary>
[TestClass]
public class JobsRegistrationShould
{
    private static readonly Type[] RunnerServices =
    [
        typeof(ScopeCoalescer), typeof(JobRunner), typeof(ClaimedJobExecutor), typeof(InMemoryJobState),
    ];

    [TestMethod]
    public void RegisterTheDurableRunnerByDefault()
    {
        // Arrange
        var services = new ServiceCollection();

        // Act
        AddJobs(services, withEngine: true, jobs =>
        {
            jobs.Schedule("rollup").Every(TimeSpan.FromMinutes(1)).Run(() => new NoopJob());
            jobs.On<ThingHappened>("notify").Run(thing => new RecordedJob(thing.Label));
        });

        // Assert — the default is today's behaviour, unchanged
        HostedServices(services).Should().Equal(
            typeof(JobTableInitializer), typeof(DurableJobScheduler), typeof(JobCleanup));
        ImplementationOf<IJobs>(services).Should().Be<DurableJobs>();
        services.Should().Contain(descriptor => descriptor.ServiceType == typeof(ClaimedJobExecutor));
        services.Should().Contain(descriptor => descriptor.ServiceType == typeof(IEventListener<ThingHappened>));
    }

    [TestMethod]
    public void RegisterTheInMemorySchedulerByDefaultWithoutAnEngine()
    {
        // Arrange
        var services = new ServiceCollection();

        // Act
        AddJobs(services, withEngine: false, jobs =>
            jobs.Schedule("rollup").Every(TimeSpan.FromMinutes(1)).Run(() => new NoopJob()));

        // Assert
        HostedServices(services).Should().Equal(typeof(JobTableInitializer), typeof(InMemoryJobScheduler));
        ImplementationOf<IJobs>(services).Should().Be<InMemoryJobs>();
    }

    [TestMethod]
    public void RegisterNoRunnerOnAManageOnlyHostWithAnEngine()
    {
        // Arrange
        var services = new ServiceCollection();

        // Act
        AddJobs(services, withEngine: true, jobs =>
        {
            jobs.ManageOnly = true;
            jobs.Schedule("rollup").Every(TimeSpan.FromMinutes(1)).Run(() => new NoopJob());
        });

        // Assert — IJobs over the shared ledger, and not one piece of what claims from it
        HostedServices(services).Should().Equal(typeof(JobTableInitializer));
        ImplementationOf<IJobs>(services).Should().Be<DurableJobs>();
        ImplementationOf<IJobStore>(services).Should().Be<DbJobStore>();
        services.Should().NotContain(descriptor => RunnerServices.Contains(descriptor.ServiceType));
        services.Should().NotContain(descriptor =>
            descriptor.ServiceType.IsGenericType
            && descriptor.ServiceType.GetGenericTypeDefinition() == typeof(IEventListener<>));
    }

    [TestMethod]
    public void RegisterNoSchedulerOnAManageOnlyHostWithoutAnEngine()
    {
        // Arrange
        var services = new ServiceCollection();

        // Act
        AddJobs(services, withEngine: false, jobs => jobs.ManageOnly = true);

        // Assert
        HostedServices(services).Should().Equal(typeof(JobTableInitializer));
        ImplementationOf<IJobs>(services).Should().Be<InMemoryJobs>();
        services.Should().NotContain(descriptor => descriptor.ServiceType == typeof(IJobStore));
    }

    [TestMethod]
    public void ReportAManageOnlyHostReadyWithNothingToArm()
    {
        // Arrange — nothing will ever report armed, so readiness must not wait for it
        var services = new ServiceCollection();
        AddJobs(services, withEngine: true, jobs => jobs.ManageOnly = true);
        using var serviceProvider = services.BuildServiceProvider();

        // Act
        var ready = serviceProvider.GetRequiredService<JobSchedulerReadiness>().Ready;

        // Assert
        ready.IsCompletedSuccessfully.Should().BeTrue();
    }

    private static void AddJobs(IServiceCollection services, bool withEngine, Action<JobsBuilder> configure)
    {
        var builder = new ConduitBuilder(services);
        if (withEngine)
        {
            builder.UseSqlite("Data Source=:memory:");
        }

        builder.AddJobs(configure);
    }

    private static List<Type?> HostedServices(IServiceCollection services)
        => [.. services
            .Where(descriptor => descriptor.ServiceType == typeof(IHostedService))
            .Select(descriptor => descriptor.ImplementationType)];

    private static Type? ImplementationOf<TService>(IServiceCollection services)
        => services.Single(descriptor => descriptor.ServiceType == typeof(TService)).ImplementationType;
}
