using AwesomeAssertions;
using Conduit.Durability.Configuration;
using Conduit.Durability.Outbox;
using Conduit.Durability.Persistence.Outbox;
using Conduit.Durability.Runtime.Outbox;
using Conduit.Durability.Tests.Support;
using Conduit.Generated;
using Conduit.Jobs;
using Conduit.Jobs.Persistence;
using Conduit.Persistence;
using Conduit.Persistence.Sqlite;
using Conduit.Runtime;
using Conduit.TestUtils;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using System.Globalization;

namespace Conduit.Durability.Tests.Integration;

/// <summary>
/// A Conduit.Jobs event trigger is a local listener of the event, so under an outbox it runs inside the outbox's
/// local delivery. Each delivery attempt is a dispatch with its own <c>MessageId</c>; the trigger keys its run on
/// <see cref="MessageContext.DeliveredMessageId"/> (the entry's id on every attempt), so a redelivery collapses.
/// </summary>
[TestClass]
public class OutboxEventTriggerShould
{
    [TestCleanup]
    public void Cleanup() => MessageContext.RestoreContext(null);

    [TestMethod]
    public async Task RunANonCoalescingTriggerOnceWhenTheRelayRedeliversAFailedFlush()
    {
        // Arrange — a sibling listener of the event throws on its first call
        await using var connectionFactory = new StubConnectionFactory();
        await using var host = await StartHostAsync(connectionFactory);
        var mediator = host.GetRequiredService<IMediator>();
        var probe = host.GetRequiredService<ListenerProbe>();
        var spy = host.GetRequiredService<TriggeredJobSpy>();
        probe.ThrowOnNextCalls(1);

        // Act — the flush's delivery writes and runs the trigger's job, but fails on the sibling, so the entry stays
        // pending; the relay then redelivers it to both listeners
        await connectionFactory.BeginTransactionAsync();
        _ = await mediator.ExecuteAsync(new RecordAuditCommand("triggered"), CancellationToken.None);
        await connectionFactory.CommitAsync();
        await host.GetRequiredService<IOutboxFlusher>().FlushAsync(CancellationToken.None);
        await WaitUntilAsync(
            connectionFactory,
            "SELECT RetryCount FROM conduit_outbox",
            value => value >= 1,
            "the failed flush delivery must leave the entry pending for the relay");
        await WaitUntilAsync(
            connectionFactory,
            $"SELECT COUNT(*) FROM conduit_job_runs WHERE Status = {(int)JobRunStatus.Completed}",
            value => value == 1,
            "the flush's delivery must have run the triggered job to completion");

        var relay = new OutboxRelay(
            host.GetRequiredService<IServiceScopeFactory>(),
            host.GetRequiredService<TransportDispatcher>(),
            Options.Create(new OutboxOptions { GracePeriod = TimeSpan.Zero, AutoCreateTable = false }),
            NullLogger<OutboxRelay>.Instance);
        await relay.ProcessPendingAsync(CancellationToken.None);
        await WaitUntilAsync(
            connectionFactory,
            "SELECT Status FROM conduit_outbox",
            value => value == (int)OutboxEntryStatus.Sent,
            "the relay must redeliver the entry and mark it sent");

        // Assert — two delivery attempts with distinct MessageIds, one run
        probe.MessageIds.Should().HaveCount(2);
        probe.MessageIds.Distinct().Should().HaveCount(2, "each delivery attempt is a dispatch with its own MessageId");
        await Task.Delay(200);
        spy.Runs.Should().Be(1, "the trigger keys its run on the delivered entry's id, which the redelivery repeats");
        (await ReadIntAsync(connectionFactory, "SELECT COUNT(*) FROM conduit_job_runs")).Should().Be(1);
        var (outboxMessageId, _) = await ReadOutboxIdsAsync(connectionFactory);
        (await ReadStringAsync(connectionFactory, "SELECT JobKey FROM conduit_job_runs")).Should().EndWith("@" + outboxMessageId);
    }

    private static async Task<TestWebHost> StartHostAsync(StubConnectionFactory connectionFactory)
    {
        var store = new DbConnectionOutboxStore(connectionFactory, new SqliteDialect());
        await store.EnsureTableExistsAsync(CancellationToken.None);

        // The relay's grace period is pushed out of reach so only the flush (and the hand-run relay) deliver, and a
        // failed flush backs off for no time, so the hand-run relay finds its entry due.
        return TestWebHost.Start(builder =>
        {
            builder.Services.AddConduit(conduit =>
            {
                conduit.UseSqlite("Data Source=:memory:");
                conduit.AddDurability(durability => durability.AddOutbox(options =>
                {
                    options.AutoCreateTable = false;
                    options.GracePeriod = TimeSpan.FromHours(1);
                    options.InitialBackoff = TimeSpan.Zero;
                }));
                conduit.AddJobs(jobs => jobs.On<AuditTrailEvent>().Run(e => new TriggeredAuditJob(e.Detail)));
            });
            builder.Services.AddSingleton<IConduitConnectionFactory>(connectionFactory);
            builder.Services.AddSingleton<ListenerProbe>();
            builder.Services.AddSingleton<TriggeredJobSpy>();
            builder.Services.AddSingleton<IConduitTransport>(new SpyTransport());
        });
    }

    private static async Task<(string MessageId, string ConversationId)> ReadOutboxIdsAsync(StubConnectionFactory connectionFactory)
    {
        using var query = connectionFactory.Connection.CreateCommand();
        query.CommandText = "SELECT MessageId, ConversationId FROM conduit_outbox";
        using var reader = await query.ExecuteReaderAsync();
        (await reader.ReadAsync()).Should().BeTrue();
        return (reader.GetString(0), reader.GetString(1));
    }

    private static async Task<int> ReadIntAsync(StubConnectionFactory connectionFactory, string sql)
    {
        using var query = connectionFactory.Connection.CreateCommand();
#pragma warning disable CA2100 // Test-owned literals.
        query.CommandText = sql;
#pragma warning restore CA2100
        return Convert.ToInt32(await query.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
    }

    private static async Task<string?> ReadStringAsync(StubConnectionFactory connectionFactory, string sql)
    {
        using var query = connectionFactory.Connection.CreateCommand();
#pragma warning disable CA2100 // Test-owned literals.
        query.CommandText = sql;
#pragma warning restore CA2100
        return await query.ExecuteScalarAsync() as string;
    }

    // The flush, the triggered job and the relay's bookkeeping all run in the background: poll for each step.
    private static async Task WaitUntilAsync(
        StubConnectionFactory connectionFactory, string sql, Func<int, bool> reached, string because)
    {
        for (var attempt = 0; attempt < 50; attempt++)
        {
            if (reached(await ReadIntAsync(connectionFactory, sql)))
            {
                return;
            }

            await Task.Delay(100);
        }

        reached(await ReadIntAsync(connectionFactory, sql)).Should().BeTrue(because);
    }
}
