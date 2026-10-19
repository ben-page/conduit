using AwesomeAssertions;
using Conduit.Audit.Tests.Support;
using Conduit.Configuration;
using Conduit.Generated;
using Conduit.Runtime;
using Conduit.TestUtils;
using Microsoft.Extensions.DependencyInjection;

namespace Conduit.Audit.Tests.Integration;

/// <summary>
/// With a dispatch retry policy, a sealed record whose context is bound to an attempt waits for that attempt to be
/// kept: an abandoned attempt's nested records are dropped, the kept attempt's written, and a top-level or independent
/// dispatch's record is written when it seals. The owner's record is the channel's last write and the publisher
/// drains in order, so once it has arrived every earlier write has too: the tests assert straight after waiting for it.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class AuditObserverRetryShould
{
    [TestCleanup]
    public void Cleanup()
    {
        MessageContext.RestoreContext(null);
    }

    [TestMethod]
    public async Task A_retried_attempts_nested_records_never_reach_the_channel()
    {
        // Arrange -- attempt 1 throws after both nested dispatches complete; attempt 2 succeeds
        await using var host = await StartHostAsync(withPolicy: true);
        var mediator = host.GetRequiredService<IMediator>();
        var sink = host.GetRequiredService<AuditRetryRecordSink>();

        // Act
        await mediator.ExecuteAsync(new AuditRetryOwnerCommand(Failures: 1));
        await sink.WaitForAsync(typeof(AuditRetryOwnerCommand), 1);
        await sink.WaitForAsync(typeof(AuditRetryNotedEvent), 1);
        await sink.WaitForAsync(typeof(AuditRetryInnerCommand), 1);

        // Assert -- the owner's record and attempt 2's two nested records, nothing from attempt 1
        sink.Of(typeof(AuditRetryOwnerCommand)).Should().ContainSingle();
        sink.Of(typeof(AuditRetryNotedEvent)).Should().ContainSingle();
        sink.Of(typeof(AuditRetryInnerCommand)).Should().ContainSingle();
    }

    [TestMethod]
    public async Task An_exhausted_dispatch_keeps_its_last_attempts_nested_records()
    {
        // Arrange -- every attempt fails, so the last attempt's failure is the dispatch's answer
        await using var host = await StartHostAsync(withPolicy: true);
        var mediator = host.GetRequiredService<IMediator>();
        var sink = host.GetRequiredService<AuditRetryRecordSink>();

        // Act
        var act = () => mediator.ExecuteAsync(new AuditRetryOwnerCommand(Failures: int.MaxValue));
        await act.Should().ThrowAsync<RetryMeException>();
        await sink.WaitForAsync(typeof(AuditRetryOwnerCommand), 1);
        await sink.WaitForAsync(typeof(AuditRetryNotedEvent), 1);
        await sink.WaitForAsync(typeof(AuditRetryInnerCommand), 1);

        // Assert -- three attempts ran, and only the last one's nested records were written
        sink.Of(typeof(AuditRetryOwnerCommand)).Should().ContainSingle();
        sink.Of(typeof(AuditRetryNotedEvent)).Should().ContainSingle();
        sink.Of(typeof(AuditRetryInnerCommand)).Should().ContainSingle();
    }

    [TestMethod]
    public async Task The_owners_one_record_carries_each_RetryAttempted()
    {
        // Arrange -- two failed attempts, then a success
        await using var host = await StartHostAsync(withPolicy: true);
        var mediator = host.GetRequiredService<IMediator>();
        var sink = host.GetRequiredService<AuditRetryRecordSink>();

        // Act
        await mediator.ExecuteAsync(new AuditRetryOwnerCommand(Failures: 2));
        await sink.WaitForAsync(typeof(AuditRetryOwnerCommand), 1);

        // Assert
        var owner = sink.Of(typeof(AuditRetryOwnerCommand)).Should().ContainSingle().Subject;
        var retries = owner.Observations.OfType<RetryAttempted>().ToList();
        retries.Select(retry => retry.Attempt).Should().Equal(1, 2);
        retries.Should().AllSatisfy(retry => retry.ExceptionType.Should().Be(typeof(RetryMeException).FullName));
    }

    [TestMethod]
    public async Task An_independent_dispatchs_record_survives_an_abandoned_outer_attempt()
    {
        // Arrange -- the outer attempt 1 sends its inner command from a fresh scope, then fails
        await using var host = await StartHostAsync(withPolicy: true);
        var mediator = host.GetRequiredService<IMediator>();
        var sink = host.GetRequiredService<AuditRetryRecordSink>();

        // Act
        await mediator.ExecuteAsync(new AuditRetryIndependentCommand(Failures: 1));
        await sink.WaitForAsync(typeof(AuditRetryInnerCommand), 2);
        await sink.WaitForAsync(typeof(AuditRetryIndependentCommand), 1);

        // Assert -- the independent dispatch is not part of the attempt, so each of the two outer attempts' records stays
        sink.Of(typeof(AuditRetryInnerCommand)).Should().HaveCount(2);
        sink.Of(typeof(AuditRetryIndependentCommand)).Should().ContainSingle();
    }

    [TestMethod]
    public async Task Without_a_policy_nested_records_are_written_as_they_seal()
    {
        // Arrange -- no retry: the owner fails once and the failure is the answer
        await using var host = await StartHostAsync(withPolicy: false);
        var mediator = host.GetRequiredService<IMediator>();
        var sink = host.GetRequiredService<AuditRetryRecordSink>();

        // Act
        var act = () => mediator.ExecuteAsync(new AuditRetryOwnerCommand(Failures: 1));
        await act.Should().ThrowAsync<RetryMeException>();
        await sink.WaitForAsync(typeof(AuditRetryOwnerCommand), 1);
        await sink.WaitForAsync(typeof(AuditRetryNotedEvent), 1);
        await sink.WaitForAsync(typeof(AuditRetryInnerCommand), 1);

        // Assert
        sink.Of(typeof(AuditRetryOwnerCommand)).Should().ContainSingle();
        sink.Of(typeof(AuditRetryNotedEvent)).Should().ContainSingle();
        sink.Of(typeof(AuditRetryInnerCommand)).Should().ContainSingle();
    }

    private static Task<TestHost> StartHostAsync(bool withPolicy)
        => TestHost.StartAsync(builder =>
        {
            builder.Services.AddSingleton<AuditRetryCalls>();
            builder.Services.AddSingleton<AuditRetryRecordSink>();
            builder.Services.AddConduit(conduit =>
            {
                conduit.AddAudit(audit => audit.BatchSize = 1);
                if (withPolicy)
                {
                    conduit.AddDispatchRetry<TestRetryPolicy>(options =>
                    {
                        options.Delays.Clear();
                        options.Delays.Add(new DispatchRetryDelay(TimeSpan.Zero, TimeSpan.Zero));
                        options.Delays.Add(new DispatchRetryDelay(TimeSpan.Zero, TimeSpan.Zero));
                    });
                }
            });
        });
}
