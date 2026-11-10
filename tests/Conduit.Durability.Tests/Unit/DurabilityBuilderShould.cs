using AwesomeAssertions;
using Conduit.Configuration;
using Conduit.Durability.Configuration;
using Conduit.Durability.Runtime;
using Conduit.Durability.Runtime.DeadLetterQueue;
using Conduit.Durability.Runtime.Inbox;
using Conduit.Durability.Runtime.Outbox;
using Conduit.Generated;
using Conduit.Middleware;
using Conduit.Persistence.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Conduit.Durability.Tests.Unit;

[TestClass]
public sealed class DurabilityBuilderShould
{
    [TestMethod]
    public void RegisterTheTableInitializerBeforeTheRelayAndTheCleanup()
    {
        // Arrange — hosted services start in registration order, and the relay and the cleanup query their tables on
        // their first pass
        var services = new ServiceCollection();

        // Act
        services.AddConduit(conduit => conduit.AddDurability(durability =>
        {
            durability.AddOutbox();
            durability.AddInbox();
        }));

        // Assert
        HostedServices(services).Should().StartWith(typeof(DurabilityTableInitializer))
            .And.Contain([typeof(OutboxRelay), typeof(InboxCleanup)]);
    }

    [TestMethod]
    public async Task RegisterOneGateWhenAddInboxIsCalledTwice()
    {
        // Arrange — two gates would share the scope's one InboxInterceptor, and the second would find the message the
        // first just recorded and discard every message as a duplicate
        var services = NewServices();

        // Act
        services.AddConduit(conduit =>
        {
            conduit.UseSqlite("Data Source=:memory:");
            conduit.AddDurability(durability => durability.AddInbox());
            conduit.AddDurability(durability => durability.AddInbox());
        });

        // Assert
        await using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();
        scope.ServiceProvider.GetServices<IInboundInterceptor>().Should().ContainSingle()
            .Which.Should().BeOfType<InboxInterceptor>();
        scope.ServiceProvider.GetServices<IInboundErrorInterceptor>().Should().ContainSingle()
            .Which.Should().BeOfType<InboxErrorInterceptor>();
        HostedServices(services).Should().ContainSingle(type => type == typeof(InboxCleanup));
    }

    [TestMethod]
    public async Task RegisterOneOutboundInterceptorWhenAddOutboxIsCalledTwice()
    {
        // Arrange
        var services = NewServices();

        // Act
        services.AddConduit(conduit =>
        {
            conduit.UseSqlite("Data Source=:memory:");
            conduit.AddDurability(durability => durability.AddOutbox());
            conduit.AddDurability(durability => durability.AddOutbox());
        });

        // Assert
        await using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();
        scope.ServiceProvider.GetServices<IOutboundInterceptor>().Should().ContainSingle()
            .Which.Should().BeOfType<OutboxCapture>();
        scope.ServiceProvider.GetServices<IOutboxFlusher>().Should().ContainSingle();
        scope.ServiceProvider.GetServices<IOutboxPendingWriter>().Should().ContainSingle();
        provider.GetRequiredService<ConduitOptions>().PipelineTypes
            .Should().ContainSingle(type => type == typeof(OutboxBehavior<,>));
        HostedServices(services).Should().ContainSingle(type => type == typeof(OutboxRelay));
    }

    [TestMethod]
    public async Task RegisterOneErrorInterceptorWhenAddDeadLetterQueueIsCalledTwice()
    {
        // Arrange
        var services = NewServices();

        // Act
        services.AddConduit(conduit =>
        {
            conduit.UseSqlite("Data Source=:memory:");
            conduit.AddDurability(durability => durability.AddDeadLetterQueue());
            conduit.AddDurability(durability => durability.AddDeadLetterQueue());
        });

        // Assert
        await using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();
        scope.ServiceProvider.GetServices<IInboundErrorInterceptor>().Should().ContainSingle()
            .Which.Should().BeOfType<DeadLetterErrorInterceptor>();
    }

    [TestMethod]
    public void ApplyEveryAddOutboxCallsOptionsToOneOutbox()
    {
        // Arrange — two modules of one host, each configuring the outbox it needs
        var services = NewServices();

        // Act
        services.AddConduit(conduit => conduit.AddDurability(durability =>
        {
            durability.AddOutbox(options => options.BatchSize = 7);
            durability.AddOutbox(options => options.MaxRetries = 4);
        }));

        // Assert
        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<OutboxOptions>>().Value;
        options.BatchSize.Should().Be(7);
        options.MaxRetries.Should().Be(4);
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public void StayCaptureOnlyWhicheverAddOutboxCallAskedForIt(bool firstCallAsks)
    {
        // Arrange — a capture-only host must never relay, whichever of two calls asked for it
        var services = NewServices();

        // Act
        services.AddConduit(conduit => conduit.AddDurability(durability =>
        {
            durability.AddOutbox(options => AskForCaptureOnlyWhen(firstCallAsks, options));
            durability.AddOutbox(options => AskForCaptureOnlyWhen(!firstCallAsks, options));
        }));

        // Assert
        HostedServices(services).Should().NotContain(typeof(OutboxRelay));
        services.Should().ContainSingle(descriptor => descriptor.ServiceType == typeof(IOutboxFlusher))
            .Which.ImplementationType.Should().Be<CaptureOnlyOutboxFlusher>();
    }

    [TestMethod]
    public async Task RelayAgainWhenALaterAddOutboxCallTurnsCaptureOnlyOff()
    {
        // Arrange — the last call that sets CaptureOnly wins, as for every other option
        var services = NewServices();

        // Act
        services.AddConduit(conduit =>
        {
            conduit.UseSqlite("Data Source=:memory:");
            conduit.AddDurability(durability =>
            {
                durability.AddOutbox(options => options.CaptureOnly = true);
                durability.AddOutbox(options => options.CaptureOnly = false);
            });
        });

        // Assert
        HostedServices(services).Should().ContainSingle(type => type == typeof(OutboxRelay));
        await using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();
        scope.ServiceProvider.GetServices<IOutboxFlusher>().Should().ContainSingle()
            .Which.Should().BeOfType<OutboxCapture>();
    }

    private static void AskForCaptureOnlyWhen(bool asks, OutboxOptions options)
    {
        if (asks)
        {
            options.CaptureOnly = true;
        }
    }

    private static ServiceCollection NewServices()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        return services;
    }

    private static List<Type?> HostedServices(ServiceCollection services)
        => services
            .Where(descriptor => descriptor.ServiceType == typeof(IHostedService))
            .Select(descriptor => descriptor.ImplementationType)
            .Where(type => type?.Assembly == typeof(DurabilityTableInitializer).Assembly)
            .ToList();
}
