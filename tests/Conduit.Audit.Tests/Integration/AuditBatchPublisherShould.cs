using AwesomeAssertions;
using Conduit.Audit.Runtime;
using Conduit.Audit.Tests.Support;
using Conduit.Generated;
using Conduit.Runtime;
using Conduit.TestUtils;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;

namespace Conduit.Audit.Tests.Integration;

/// <summary>
/// An <see cref="AuditBatch"/> listener's failure is the drain loop's to log: it never escapes the background service, so
/// it neither stops the host nor ends audit for the rest of the process. On stop the publisher drains what is queued,
/// within its shutdown grace, and a sweep that throws is logged, never the end of the service.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class AuditBatchPublisherShould
{
    [TestCleanup]
    public void Cleanup()
    {
        MessageContext.RestoreContext(null);
    }

    [TestMethod]
    public async Task StampABatchAndItsRecordsWithTheHostsClock()
    {
        // Arrange — a host whose clock is years away from the machine's
        var now = new DateTimeOffset(2030, 1, 1, 12, 0, 0, TimeSpan.Zero);
        await using var host = await TestHost.StartAsync(builder =>
        {
            builder.Services.AddSingleton<TimeProvider>(new FakeTimeProvider(now));
            builder.Services.AddSingleton<MessageIdCapture>();
            builder.Services.AddSingleton<PublishedAuditBatches>();
            builder.Services.AddConduit(conduit => conduit.AddAudit(audit => audit.BatchSize = 1));
        });
        var published = host.GetRequiredService<PublishedAuditBatches>();

        // Act
        await host.GetRequiredService<IMediator>().ExecuteAsync(new AuditTestCommand("stamped"));
        await published.WaitForFirstAsync();

        // Assert
        var batch = published.Batches[0];
        batch.BatchTimestamp.Should().Be(now.UtcDateTime);
        batch.Records.Should().ContainSingle().Which.Timestamp.Should().Be(now.UtcDateTime);
    }

    [TestMethod]
    public async Task KeepTheHostRunningAndPublishTheNextBatchWhenAListenerThrows()
    {
        // Arrange
        await using var host = await TestHost.StartAsync(builder =>
        {
            builder.Services.AddSingleton<MessageIdCapture>();
            builder.Services.AddSingleton<AuditBatchFailures>();
            builder.Services.AddConduit(conduit => conduit.AddAudit(audit => audit.BatchSize = 1));
        });
        var mediator = host.GetRequiredService<IMediator>();
        var failures = host.GetRequiredService<AuditBatchFailures>();
        var lifetime = host.GetRequiredService<IHostApplicationLifetime>();

        // Act -- the first command's batch fails its listener; the second command's batch must still be published
        await mediator.ExecuteAsync(new AuditTestCommand("first"));
        await failures.WaitForCountAsync(1);
        await mediator.ExecuteAsync(new AuditTestCommand("second"));
        await failures.WaitForCountAsync(2);

        // Assert
        lifetime.ApplicationStopping.IsCancellationRequested.Should().BeFalse(
            "a failed audit listener must never stop the host");
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task DrainOnStop()
    {
        // Arrange — the first batch's listener holds the publisher; two more records queue behind it
        await using var host = await TestHost.StartAsync(builder =>
        {
            builder.Services.AddSingleton<MessageIdCapture>();
            builder.Services.AddSingleton<PublishedAuditBatches>();
            builder.Services.AddSingleton<AuditBatchGate>();
            builder.Services.AddConduit(conduit => conduit.AddAudit(audit => audit.BatchSize = 1));
        });
        var mediator = host.GetRequiredService<IMediator>();
        var gate = host.GetRequiredService<AuditBatchGate>();
        var published = host.GetRequiredService<PublishedAuditBatches>();
        var publisher = host.GetRequiredService<IEnumerable<IHostedService>>().OfType<AuditBatchPublisher>().Single();
        await mediator.ExecuteAsync(new AuditTestCommand("first"));
        await gate.WaitUntilHeldAsync();
        await mediator.ExecuteAsync(new AuditTestCommand("second"));
        await mediator.ExecuteAsync(new AuditTestCommand("third"));

        // Act — the stop begins with a batch in flight and two records queued; only then does the listener return
        var stopping = publisher.StopAsync(CancellationToken.None);
        gate.Release();
        await stopping;

        // Assert
        published.Batches.SelectMany(batch => batch.Records)
            .Select(record => record.Payload!.Value.GetProperty("value").GetString())
            .Should().BeEquivalentTo(["first", "second", "third"]);
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task LogTheRecordsLeftWhenTheGraceEnds()
    {
        // Arrange — a listener that never returns within the grace, one batch in flight and two records behind it
        var spy = new SpyLoggerProvider();
        await using var host = await TestHost.StartAsync(builder =>
        {
            builder.Logging.AddProvider(spy);
            builder.Services.AddSingleton<MessageIdCapture>();
            builder.Services.AddSingleton<AuditBatchGate>();
            builder.Services.AddConduit(conduit => conduit.AddAudit(audit =>
            {
                audit.BatchSize = 1;
                audit.ShutdownGrace = TimeSpan.FromMilliseconds(100);
            }));
        });
        var mediator = host.GetRequiredService<IMediator>();
        var gate = host.GetRequiredService<AuditBatchGate>();
        var publisher = host.GetRequiredService<IEnumerable<IHostedService>>().OfType<AuditBatchPublisher>().Single();
        await mediator.ExecuteAsync(new AuditTestCommand("first"));
        await gate.WaitUntilHeldAsync();
        await mediator.ExecuteAsync(new AuditTestCommand("second"));
        await mediator.ExecuteAsync(new AuditTestCommand("third"));
        using var hostDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        // Act — the grace ends long before the host's deadline
        await publisher.StopAsync(hostDeadline.Token);
        var stoppedBeforeTheHostDeadline = !hostDeadline.IsCancellationRequested;
        gate.Release();

        // Assert
        stoppedBeforeTheHostDeadline.Should().BeTrue("the shutdown grace bounds the drain");
        spy.Entries.Should().ContainSingle(entry => entry.EventId.Id == 520)
            .Which.Should().Match<SpyLogEntry>(entry =>
                entry.Level == LogLevel.Warning && entry.EventId.Name == "AuditRecordsAbandoned" && entry.Message.Contains("3 audit record(s)"));
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task LogAnAbandonedBatchOnceWhenTheHostDisposesAfterTheStop()
    {
        // Arrange — a listener that never returns within the grace holds one batch in flight
        var spy = new SpyLoggerProvider();
        await using var host = await TestHost.StartAsync(builder =>
        {
            builder.Logging.AddProvider(spy);
            builder.Services.AddSingleton<MessageIdCapture>();
            builder.Services.AddSingleton<AuditBatchGate>();
            builder.Services.AddConduit(conduit => conduit.AddAudit(audit =>
            {
                audit.BatchSize = 1;
                audit.ShutdownGrace = TimeSpan.FromMilliseconds(100);
            }));
        });
        var gate = host.GetRequiredService<AuditBatchGate>();
        await host.GetRequiredService<IMediator>().ExecuteAsync(new AuditTestCommand("held"));
        await gate.WaitUntilHeldAsync();

        // Act — the stop's grace abandons the held batch, and the host disposes while that publish still runs
        try
        {
            await host.StopAsync(CancellationToken.None);
            await host.DisposeAsync();
        }
        finally
        {
            gate.Release();
        }

        // Assert — the batch abandoned once is logged once
        spy.Entries.Where(entry => entry.EventId.Id == 520).Should().ContainSingle()
            .Which.Message.Should().Contain("1 audit record(s)");
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task PublishAProvisionalThenTheFinalRecordOfACommandSilentPastTheTimeout()
    {
        // Arrange — a command handler held past a short stale timeout, observing nothing meanwhile, as a job body does
        await using var host = await TestHost.StartAsync(builder =>
        {
            builder.Services.AddSingleton<PublishedAuditBatches>();
            builder.Services.AddSingleton<AuditCommandGate>();
            builder.Services.AddConduit(conduit => conduit.AddAudit(audit =>
            {
                audit.BatchSize = 1;
                audit.StaleGroupTimeout = TimeSpan.FromMilliseconds(50);
            }));
        });
        var gate = host.GetRequiredService<AuditCommandGate>();
        var published = host.GetRequiredService<PublishedAuditBatches>();
        var executing = host.GetRequiredService<IMediator>().ExecuteAsync(new HeldAuditCommand("silent"));
        await gate.WaitUntilHeldAsync();
        await published.WaitForRecordsAsync(1);

        // Act — the handler returns after its provisional record was published
        gate.Release();
        await executing;
        await published.WaitForRecordsAsync(2);

        // Assert
        var records = published.Batches.SelectMany(batch => batch.Records).ToList();
        records.Should().HaveCount(2);
        records[0].Observations.Should().NotContain(observation => observation is DispatchFailed || observation is DispatchCompleted);
        records[0].Observations[^1].Should().BeOfType<CustomObservation>()
            .Which.Name.Should().Be("Conduit.Audit.StaleGroupEviction");
        records[1].MessageId.Should().Be(records[0].MessageId);
        records[1].Observations.Should().NotContain(observation => observation is DispatchFailed);
        records[1].Observations[^1].Should().BeOfType<DispatchCompleted>();
    }

    [TestMethod]
    public async Task LogTheRecordsADisposeWithoutAStopAbandons()
    {
        // Arrange — a provider whose publisher was never started, with one record queued
        var spy = new SpyLoggerProvider();
        var services = new ServiceCollection();
        services.AddLogging(logging => logging.AddProvider(spy));
        services.AddSingleton<MessageIdCapture>();
        services.AddConduit(conduit => conduit.AddAudit());
        var provider = services.BuildServiceProvider();
        await provider.GetRequiredService<IMediator>().ExecuteAsync(new AuditTestCommand("queued"));
        provider.GetServices<IHostedService>().OfType<AuditBatchPublisher>().Should().ContainSingle();

        // Act
        await provider.DisposeAsync();

        // Assert
        spy.Entries.Should().ContainSingle(entry => entry.EventId.Id == 520)
            .Which.Message.Should().Contain("1 audit record(s)");
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task KeepSweepingAfterASweepThrows()
    {
        // Arrange — every sweep throws, reading a broken clock
        var spy = new SpyLoggerProvider();
        await using var host = await TestHost.StartAsync(builder =>
        {
            builder.Logging.AddProvider(spy);
            builder.Services.AddSingleton<TimeProvider>(new ThrowingTimeProvider());
            builder.Services.AddConduit(conduit => conduit.AddAudit(audit => audit.StaleGroupTimeout = TimeSpan.FromMilliseconds(50)));
        });
        var lifetime = host.GetRequiredService<IHostApplicationLifetime>();

        // Act — wait for a second failed sweep, which only a sweep loop still running can log
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (spy.Entries.Count(entry => entry.EventId.Id == 521) < 2 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10);
        }

        // Assert
        spy.Entries.Where(entry => entry.EventId.Id == 521).Should().HaveCountGreaterThanOrEqualTo(2)
            .And.AllSatisfy(entry => entry.Exception.Should().BeOfType<InvalidOperationException>());
        lifetime.ApplicationStopping.IsCancellationRequested.Should().BeFalse("a failed sweep must never stop the host");
    }
}
