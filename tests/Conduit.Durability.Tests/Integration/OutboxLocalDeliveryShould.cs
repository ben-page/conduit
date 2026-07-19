using AwesomeAssertions;
using Conduit.Durability.Configuration;
using Conduit.Durability.Persistence.Outbox;
using Conduit.Durability.Runtime.Outbox;
using Conduit.Durability.Tests.Support;
using Conduit.Generated;
using Conduit.Persistence;
using Conduit.Persistence.Sqlite;
using Conduit.Runtime;
using Conduit.TestUtils;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using System.Globalization;

namespace Conduit.Durability.Tests.Integration;

[TestClass]
public class OutboxLocalDeliveryShould
{
    [TestMethod]
    public async Task RunZeroListenersWhenTheTransactionNeverCommits()
    {
        // Arrange
        await using var connectionFactory = new StubConnectionFactory();
        var store = new DbConnectionOutboxStore(connectionFactory, new SqliteDialect());
        await store.EnsureTableExistsAsync(CancellationToken.None);

        await using var host = TestWebHost.Start(builder =>
        {
            builder.Services.AddConduit(conduit =>
            {
                conduit.UseSqlite("Data Source=:memory:");
                conduit.AddDurability(durability => durability.AddOutbox(options => options.AutoCreateTable = false));
            });
            builder.Services.AddSingleton<IConduitConnectionFactory>(connectionFactory);
            builder.Services.AddSingleton<ListenerProbe>();
        });

        var mediator = host.GetRequiredService<IMediator>();
        var probe = host.GetRequiredService<ListenerProbe>();

        // Act — command publishes the F&F event inside an ambient transaction, then the txn ROLLS BACK
        await connectionFactory.BeginTransactionAsync();
        _ = await mediator.ExecuteAsync(new RecordAuditCommand("doomed"), CancellationToken.None);
        await connectionFactory.RollbackAsync();

        // Give the background dispatcher time to prove the negative: nothing was ever enqueued.
        await Task.Delay(300);

        // Assert — capture skipped the local enqueue; rollback discarded the outbox row; zero deliveries
        probe.Count.Should().Be(0, "a rolled-back command's captured event must never reach a listener");
        using var query = connectionFactory.Connection.CreateCommand();
        query.CommandText = "SELECT COUNT(*) FROM conduit_outbox";
        Convert.ToInt32(await query.ExecuteScalarAsync(), CultureInfo.InvariantCulture).Should().Be(0);
    }

    [TestMethod]
    public async Task DeliverLocalListenersExactlyOnceOnFlushThenTransportsThenMarkSent()
    {
        // Arrange — host as in RunZeroListenersWhenTheTransactionNeverCommits, plus the spy transport
        await using var connectionFactory = new StubConnectionFactory();
        var store = new DbConnectionOutboxStore(connectionFactory, new SqliteDialect());
        await store.EnsureTableExistsAsync(CancellationToken.None);

        var spyTransport = new SpyTransport();

        await using var host = TestWebHost.Start(builder =>
        {
            builder.Services.AddConduit(conduit =>
            {
                conduit.UseSqlite("Data Source=:memory:");
                conduit.AddDurability(durability => durability.AddOutbox(options => options.AutoCreateTable = false));
            });
            builder.Services.AddSingleton<IConduitConnectionFactory>(connectionFactory);
            builder.Services.AddSingleton<ListenerProbe>();
            builder.Services.AddSingleton<IConduitTransport>(spyTransport);
        });

        var mediator = host.GetRequiredService<IMediator>();
        var probe = host.GetRequiredService<ListenerProbe>();

        // Act
        await connectionFactory.BeginTransactionAsync();
        _ = await mediator.ExecuteAsync(new RecordAuditCommand("kept"), CancellationToken.None);
        await connectionFactory.CommitAsync();

        probe.Count.Should().Be(0, "committed but not yet flushed — listeners must not have run");

        var flusher = host.GetRequiredService<IOutboxFlusher>();
        await flusher.FlushAsync(CancellationToken.None);

        // Assert — THE contract pin: FlushAsync schedules delivery on the background dispatcher
        // and returns without waiting for it, so the caller must never observe delivery having
        // already happened at this point (restores [FireAndForget]'s "caller returns immediately"
        // contract under transactional capture).
        probe.Count.Should().Be(0, "FlushAsync must schedule background delivery and return without waiting for it");

        using var query = connectionFactory.Connection.CreateCommand();
        query.CommandText = "SELECT Status FROM conduit_outbox";

        // The background dispatcher drains the enqueued work item on its own hosted-service loop
        // (TestWebHost starts it for real) — bounded-poll for it. Poll on the terminal DB state
        // rather than probe.Count: MarkSentAsync is the LAST step of the delivery routine, strictly
        // after local dispatch and the transport send, so once Sent is observed the probe/transport
        // assertions below cannot be racing the still-in-flight work item.
        var sent = false;
        for (var attempt = 0; attempt < 50 && !sent; attempt++)
        {
            sent = Convert.ToInt32(await query.ExecuteScalarAsync(), CultureInfo.InvariantCulture) == (int)OutboxEntryStatus.Sent;
            if (!sent)
            {
                await Task.Delay(100);
            }
        }

        sent.Should().BeTrue("the background dispatcher must eventually deliver the flushed entry and mark it sent");
        probe.Count.Should().Be(1);
        spyTransport.SentEvents.Should().ContainSingle(e => e.Type.Contains("AuditTrailEvent"));
    }

    [TestMethod]
    public async Task RedeliverThroughTheRelayWhenTheFlushNeverRan()
    {
        // Arrange — host as in DeliverLocalListenersExactlyOnceOnFlushThenTransportsThenMarkSent
        // (probe + spy transport registered); this time the relay redelivers because the flush
        // never ran (simulating a crash between commit and IOutboxFlusher.FlushAsync).
        await using var connectionFactory = new StubConnectionFactory();
        var store = new DbConnectionOutboxStore(connectionFactory, new SqliteDialect());
        await store.EnsureTableExistsAsync(CancellationToken.None);

        var spyTransport = new SpyTransport();

        await using var host = TestWebHost.Start(builder =>
        {
            builder.Services.AddConduit(conduit =>
            {
                conduit.UseSqlite("Data Source=:memory:");
                conduit.AddDurability(durability => durability.AddOutbox(options => options.AutoCreateTable = false));
            });
            builder.Services.AddSingleton<IConduitConnectionFactory>(connectionFactory);
            builder.Services.AddSingleton<ListenerProbe>();
            builder.Services.AddSingleton<IConduitTransport>(spyTransport);
        });

        var mediator = host.GetRequiredService<IMediator>();
        var probe = host.GetRequiredService<ListenerProbe>();

        // Act — commit but simulate a crash before IOutboxFlusher.FlushAsync
        await connectionFactory.BeginTransactionAsync();
        _ = await mediator.ExecuteAsync(new RecordAuditCommand("crashed-before-flush"), CancellationToken.None);
        await connectionFactory.CommitAsync();

        // Resolve the host's IServiceScopeFactory + singleton TransportDispatcher (not a rebuilt
        // one) so listener resolution reaches the host's ListenerProbe and the spy transport is
        // included in the dispatch.
        var relay = new OutboxRelay(
            host.GetRequiredService<IServiceScopeFactory>(),
            host.GetRequiredService<TransportDispatcher>(),
            Options.Create(new OutboxOptions { GracePeriod = TimeSpan.Zero, AutoCreateTable = false }),
            NullLogger<OutboxRelay>.Instance);
        await relay.ProcessPendingAsync(CancellationToken.None);

        // Assert — the relay delivered locally and marked sent
        probe.Count.Should().Be(1, "the relay must redeliver local listeners for entries the flush never handled");
        using var query = connectionFactory.Connection.CreateCommand();
        query.CommandText = "SELECT Status FROM conduit_outbox";
        Convert.ToInt32(await query.ExecuteScalarAsync(), CultureInfo.InvariantCulture)
            .Should().Be((int)OutboxEntryStatus.Sent);
    }

    [TestMethod]
    public async Task BypassCaptureForImmediateEventsEvenInsideATransaction()
    {
        // Arrange — host as before (probe registered; no transport needed)
        await using var connectionFactory = new StubConnectionFactory();
        var store = new DbConnectionOutboxStore(connectionFactory, new SqliteDialect());
        await store.EnsureTableExistsAsync(CancellationToken.None);

        await using var host = TestWebHost.Start(builder =>
        {
            builder.Services.AddConduit(conduit =>
            {
                conduit.UseSqlite("Data Source=:memory:");
                conduit.AddDurability(durability => durability.AddOutbox(options => options.AutoCreateTable = false));
            });
            builder.Services.AddSingleton<IConduitConnectionFactory>(connectionFactory);
            builder.Services.AddSingleton<ListenerProbe>();
        });

        var mediator = host.GetRequiredService<IMediator>();
        var probe = host.GetRequiredService<ListenerProbe>();

        // Act — open transaction, publish the Immediate event, NEVER commit
        await connectionFactory.BeginTransactionAsync();
        await mediator.PublishAsync(new LockoutStyleEvent("must-survive-rollback"), CancellationToken.None);
        await connectionFactory.RollbackAsync();

        // Assert — delivered via the background dispatcher despite the rollback; nothing stored
        var delivered = false;
        for (var attempt = 0; attempt < 50 && !delivered; attempt++)
        {
            delivered = probe.Count == 1;
            await Task.Delay(100);
        }

        delivered.Should().BeTrue("Immediate declares rollback-survival by intent — delivery must not depend on the transaction");
        using var query = connectionFactory.Connection.CreateCommand();
        query.CommandText = "SELECT COUNT(*) FROM conduit_outbox";
        Convert.ToInt32(await query.ExecuteScalarAsync(), CultureInfo.InvariantCulture).Should().Be(0);
    }
}
