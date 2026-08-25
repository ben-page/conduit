using AwesomeAssertions;
using Conduit.Jobs.Persistence;
using Conduit.Jobs.Tests.Support;
using Conduit.Runtime;
using Microsoft.Extensions.Time.Testing;

namespace Conduit.Jobs.Tests.Integration;

[TestClass]
public class EventTriggerShould
{
    private static readonly DateTimeOffset Start = new(2026, 8, 25, 1, 59, 0, TimeSpan.Zero);

    [TestMethod]
    public async Task RunTheJobWhenTheTriggeringEventIsPublished()
    {
        // Arrange
        var spy = new JobSpy();
        await using var database = SharedSqliteDatabase.Create();
        await using var host = await StartHostAsync(database, new FakeTimeProvider(Start), spy);
        var mediator = host.GetRequiredService<IMediator>();

        // Act
        await mediator.PublishAsync(new ThingHappened("receipt"));

        // Assert — the event type has no listener anywhere in the compilation, only one registered at
        // runtime, so this also pins that the generated mediator resolves listeners per event type
        await spy.WaitForCountAsync(1);
        spy.Started.Should().ContainSingle(label => label == "receipt");
    }

    [TestMethod]
    public async Task RunOnceWhenTheSameEventIsDeliveredTwice()
    {
        // Arrange
        var spy = new JobSpy();
        await using var database = SharedSqliteDatabase.Create();
        await using var host = await StartHostAsync(database, new FakeTimeProvider(Start), spy);
        var wireMediator = host.GetRequiredService<IWireMediator>();
        var envelope = TestEnvelope.For(new ThingHappened("receipt"));

        // Act — the same MessageId twice, as a broker redelivery produces
        await DeliverWithMessageIdAsync(wireMediator, envelope, "msg-1");
        await DeliverWithMessageIdAsync(wireMediator, envelope, "msg-1");

        // Assert — the deterministic run key makes the trigger idempotent, with no dedup store
        await spy.WaitForCountAsync(1);
        await Task.Delay(200);
        spy.Count.Should().Be(1);
    }

    [TestMethod]
    public async Task WriteADelayedTriggerAsPendingRatherThanClaimed()
    {
        // Arrange
        var spy = new JobSpy();
        await using var database = SharedSqliteDatabase.Create();
        await using var host = await StartDelayedHostAsync(database, new FakeTimeProvider(Start), spy);

        // Act
        await host.GetRequiredService<IMediator>().PublishAsync(new CartAbandoned("cart-1"));

        // Assert — written claimed, the claim would look abandoned after ClaimTimeout and another
        // host would take it, collapsing a thirty-minute delay into two
        var run = await JobsDatabaseProbe.WaitForOnlyRunAsync(database);
        run.Status.Should().Be(JobRunStatus.Pending);
        run.ClaimToken.Should().BeNull();
        run.RunAt.Should().Be(Start.AddMinutes(30));
        spy.StartedCount.Should().Be(0, "the job is not due for another half hour");
    }

    [TestMethod]
    public async Task RunADelayedTriggerOnceItComesDue()
    {
        // Arrange
        var timeProvider = new FakeTimeProvider(Start);
        var spy = new JobSpy();
        await using var database = SharedSqliteDatabase.Create();
        await using var host = await StartDelayedHostAsync(database, timeProvider, spy);
        await host.GetRequiredService<IMediator>().PublishAsync(new CartAbandoned("cart-1"));
        await JobsDatabaseProbe.WaitForOnlyRunAsync(database);

        // Act — the sweep, not the triggering host, is what eventually runs it
        await FakeClock.AdvanceUntilAsync(
            timeProvider, TimeSpan.FromMinutes(5), () => Task.FromResult(spy.Count > 0));

        // Assert
        spy.Count.Should().Be(1);
        spy.Started.Should().ContainSingle(label => label == "cart-1");
    }

    private static Task<Conduit.TestUtils.TestHost> StartHostAsync(
        SharedSqliteDatabase database, FakeTimeProvider timeProvider, JobSpy spy)
        => JobsTestHost.StartAsync(
            jobs => jobs.On<ThingHappened>().Run(thing => new RecordedJob(thing.Label)),
            spy,
            timeProvider,
            database.ConnectionString);

    private static Task<Conduit.TestUtils.TestHost> StartDelayedHostAsync(
        SharedSqliteDatabase database, FakeTimeProvider timeProvider, JobSpy spy)
        => JobsTestHost.StartAsync(
            jobs => jobs.On<CartAbandoned>()
                .After(TimeSpan.FromMinutes(30))
                .Run(cart => new RecordedJob(cart.CartId)),
            spy,
            timeProvider,
            database.ConnectionString);

    private static async Task DeliverWithMessageIdAsync(
        IWireMediator wireMediator, DispatchEnvelope envelope, string messageId)
    {
        var previous = MessageContext.Current;
        try
        {
            MessageContext.StartDispatchFromTransport(messageId, causationId: null, conversationId: messageId);
            await wireMediator.HandleWireEventAsync(envelope, CancellationToken.None);
        }
        finally
        {
            MessageContext.RestoreContext(previous);
        }
    }
}
