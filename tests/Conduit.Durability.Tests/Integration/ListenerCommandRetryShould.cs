using AwesomeAssertions;
using Conduit.Configuration;
using Conduit.Durability.Configuration;
using Conduit.Durability.Persistence.Outbox;
using Conduit.Durability.Tests.Support;
using Conduit.Generated;
using Conduit.Persistence;
using Conduit.Persistence.Sqlite;
using Conduit.TestUtils;
using Microsoft.Extensions.DependencyInjection;

namespace Conduit.Durability.Tests.Integration;

/// <summary>A command an outbox listener sends goes through <c>ConduitDispatch</c>, so it is retried in fresh scopes.</summary>
[TestClass]
public class ListenerCommandRetryShould
{
    [TestMethod]
    public async Task Retry_a_command_sent_by_a_listener_the_post_commit_flush_delivered()
    {
        // Arrange -- the relay's grace period is out of reach, so only the flush can deliver the event
        await using var connectionFactory = new StubConnectionFactory();
        await new DbConnectionOutboxStore(connectionFactory, new SqliteDialect()).EnsureTableExistsAsync(CancellationToken.None);
        await using var host = TestWebHost.Start(builder =>
        {
            builder.Services.AddConduit(conduit =>
            {
                conduit.UseSqlite("Data Source=:memory:");
                conduit.AddDispatchRetry<TestRetryPolicy>(options =>
                {
                    options.Delays.Clear();
                    options.Delays.Add(new DispatchRetryDelay(TimeSpan.Zero, TimeSpan.Zero));
                    options.Delays.Add(new DispatchRetryDelay(TimeSpan.Zero, TimeSpan.Zero));
                });
                conduit.AddPipeline(typeof(CommitAndFlushBehavior<,>));
                conduit.AddDurability(durability => durability.AddOutbox(options =>
                {
                    options.AutoCreateTable = false;
                    options.GracePeriod = TimeSpan.FromHours(1);
                }));
            });
            builder.Services.AddSingleton<IConduitConnectionFactory>(connectionFactory);
            builder.Services.AddSingleton(connectionFactory);
            builder.Services.AddSingleton<RetryProbe>();
            builder.Services.AddScoped<ScopeProbe>();
        });
        var probe = host.GetRequiredService<RetryProbe>();

        // Act
        await host.GetRequiredService<IMediator>().ExecuteAsync(new RelayCommand(Failures: 1), CancellationToken.None);
        await probe.WaitForAsync(expected: 2, TimeSpan.FromSeconds(5));

        // Assert -- the listener's command ran twice, each attempt in a scope of its own
        probe.Sightings.Select(sighting => sighting.AttemptNumber).Should().Equal(1, 2);
        probe.Sightings.Select(sighting => sighting.ScopeId).Distinct().Should().HaveCount(2);
    }
}
