using System.Text.Json;
using AwesomeAssertions;
using Conduit.Configuration;
using Conduit.Generated;
using Conduit.Runtime;
using Conduit.TestUtils;
using Conduit.Tests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace Conduit.Tests.Integration;

/// <summary>
/// The generated mediator's dispatch boundary: with <see cref="ConduitBuilder.AddDispatchRetry{TPolicy}"/>, every
/// handler-running <c>ConduitDispatch</c> method (in-process and wire) runs each attempt in a fresh scope; a dispatch
/// from the attempt's own scope is nested in it, any other is independent; without a policy nothing changes.
/// </summary>
[TestClass]
public class DispatchRetryShould
{
    private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(5);

    [TestMethod]
    public async Task A_command_retried_after_a_retryable_failure_returns_the_second_attempts_result()
    {
        // Arrange
        await using var host = await StartHostAsync(withPolicy: true);
        var mediator = host.GetRequiredService<IMediator>();
        var probe = host.GetRequiredService<RetryProbe>();

        // Act
        var result = await mediator.ExecuteAsync(new FlakyCommand(Failures: 1));

        // Assert -- the handler and the behavior are resolved again for attempt 2, in a scope of its own
        var handled = probe.At(nameof(FlakyCommand));
        handled.Select(sighting => sighting.AttemptNumber).Should().Equal(1, 2);
        handled[0].ScopeId.Should().NotBe(handled[1].ScopeId);
        result.Should().Be(handled[1].ScopeId);

        var behaved = probe.At(BehaviorSite(nameof(FlakyCommand)));
        behaved.Select(sighting => sighting.ScopeId).Should().Equal(handled.Select(sighting => sighting.ScopeId));
        behaved.Select(sighting => sighting.ScopeId)
            .Should().NotContain(host.GetRequiredService<AttemptProbe>().Id, "every attempt is fresh, the first included");
    }

    [TestMethod]
    public async Task A_void_command_and_a_query_are_retried()
    {
        // Arrange
        await using var host = await StartHostAsync(withPolicy: true);
        var mediator = host.GetRequiredService<IMediator>();
        var probe = host.GetRequiredService<RetryProbe>();

        // Act
        await mediator.ExecuteAsync(new FlakyVoidCommand(Failures: 1));
        var queried = await mediator.FetchAsync(new FlakyQuery(Failures: 1));

        // Assert
        var voided = probe.At(nameof(FlakyVoidCommand));
        voided.Select(sighting => sighting.AttemptNumber).Should().Equal(1, 2);
        voided[0].ScopeId.Should().NotBe(voided[1].ScopeId);

        var fetched = probe.At(nameof(FlakyQuery));
        fetched.Select(sighting => sighting.AttemptNumber).Should().Equal(1, 2);
        fetched[0].ScopeId.Should().NotBe(fetched[1].ScopeId);
        queried.Should().Be(fetched[1].ScopeId);
    }

    [TestMethod]
    public async Task A_wire_request_is_retried()
    {
        // Arrange -- a scope per inbound message and a context started from the transport, as a transport does
        await using var host = await StartHostAsync(withPolicy: true);
        var probe = host.GetRequiredService<RetryProbe>();
        await using var messageScope = host.GetRequiredService<IServiceScopeFactory>().CreateAsyncScope();
        var wireMediator = messageScope.ServiceProvider.GetRequiredService<IWireMediator>();
        var envelope = new DispatchEnvelope(
            typeof(FlakyCommand).FullName!,
            JsonSerializer.SerializeToElement(new FlakyCommand(Failures: 1), ConduitRuntime.JsonOptions));
        MessageContext.StartDispatchFromTransport("wire-retry-message", null, "wire-retry-conversation");

        // Act
        var result = await wireMediator.HandleWireRequestAsync(envelope, CancellationToken.None);

        // Assert
        result.Error.Should().BeNull();
        var handled = probe.At(nameof(FlakyCommand));
        handled.Select(sighting => sighting.AttemptNumber).Should().Equal(1, 2);
        handled[0].ScopeId.Should().NotBe(handled[1].ScopeId);
        handled.Select(sighting => sighting.MessageId).Should().AllBe("wire-retry-message");
    }

    [TestMethod]
    public async Task ExecuteVoidWire_is_retried()
    {
        // Arrange
        await using var host = await StartHostAsync(withPolicy: true);
        var probe = host.GetRequiredService<RetryProbe>();
        await using var messageScope = host.GetRequiredService<IServiceScopeFactory>().CreateAsyncScope();
        var wireMediator = messageScope.ServiceProvider.GetRequiredService<IWireMediator>();
        var envelope = new DispatchEnvelope(
            typeof(FlakyFireAndForgetCommand).FullName!,
            JsonSerializer.SerializeToElement(new FlakyFireAndForgetCommand(Failures: 1), ConduitRuntime.JsonOptions));
        MessageContext.StartDispatchFromTransport("wire-ff-retry-message", null, "wire-ff-retry-conversation");

        // Act
        var result = await wireMediator.HandleWireRequestAsync(envelope, CancellationToken.None);

        // Assert -- the wire path runs a [FireAndForget] handler inline, so its attempts are retried like any other
        result.Error.Should().BeNull();
        var handled = probe.At(nameof(FlakyFireAndForgetCommand));
        handled.Select(sighting => sighting.AttemptNumber).Should().Equal(1, 2);
        handled[0].ScopeId.Should().NotBe(handled[1].ScopeId);
    }

    [TestMethod]
    public async Task A_nested_dispatch_shares_the_attempts_scope()
    {
        // Arrange -- the inner command fails retryably once; nested, it is not retried on its own but fails the attempt
        await using var host = await StartHostAsync(withPolicy: true);
        var mediator = host.GetRequiredService<IMediator>();
        var probe = host.GetRequiredService<RetryProbe>();

        // Act
        await mediator.ExecuteAsync(new NestingCommand(InnerFailures: 1));

        // Assert -- the outer attempt is retried; each attempt's inner dispatch ran in that attempt's own scope
        var outer = probe.At(nameof(NestingCommand));
        var inner = probe.At(nameof(InnerCommand));
        outer.Select(sighting => sighting.AttemptNumber).Should().Equal(1, 2);
        inner.Select(sighting => sighting.AttemptNumber).Should().Equal(1, 2);
        inner.Select(sighting => sighting.ScopeId).Should().Equal(outer.Select(sighting => sighting.ScopeId));
        probe.All().Select(sighting => sighting.ScopeId).Distinct()
            .Should().HaveCount(2, "two attempts create only two scopes");
    }

    [TestMethod]
    public async Task A_nested_wire_dispatch_shares_the_attempts_scope()
    {
        // Arrange -- the outer command sends an item through its injected IWireMediator, then fails retryably once
        await using var host = await StartHostAsync(withPolicy: true);
        var mediator = host.GetRequiredService<IMediator>();
        var probe = host.GetRequiredService<RetryProbe>();

        // Act
        await mediator.ExecuteAsync(new WireNestingCommand(Failures: 1));

        // Assert -- the item ran once per outer attempt, in that attempt's own scope, with no attempts of its own
        var outer = probe.At(nameof(WireNestingCommand));
        var item = probe.At(nameof(InnerCommand));
        outer.Select(sighting => sighting.AttemptNumber).Should().Equal(1, 2);
        item.Select(sighting => sighting.AttemptNumber).Should().Equal(1, 2);
        item.Select(sighting => sighting.ScopeId).Should().Equal(outer.Select(sighting => sighting.ScopeId));
        item.Select(sighting => sighting.MessageId).Should().NotIntersectWith(outer.Select(sighting => sighting.MessageId));
        probe.All().Select(sighting => sighting.ScopeId).Distinct()
            .Should().HaveCount(2, "two attempts create only two scopes");
    }

    [TestMethod]
    public async Task A_dispatch_from_a_fresh_scope_inside_an_attempt_is_independent()
    {
        // Arrange
        await using var host = await StartHostAsync(withPolicy: true);
        var mediator = host.GetRequiredService<IMediator>();
        var probe = host.GetRequiredService<RetryProbe>();

        // Act
        await mediator.ExecuteAsync(new IndependentCommand(InnerFailures: 1));

        // Assert -- the outer command ran once; its inner command got attempts of its own, each in its own scope
        var outer = probe.At(nameof(IndependentCommand)).Should().ContainSingle().Subject;
        var freshScope = probe.At(IndependentCommandHandler.FreshScopeSite).Should().ContainSingle().Subject;
        var inner = probe.At(nameof(InnerCommand));
        inner.Select(sighting => sighting.AttemptNumber).Should().Equal(1, 2);
        inner[0].ScopeId.Should().NotBe(inner[1].ScopeId);
        inner.Select(sighting => sighting.ScopeId).Should().NotContain([outer.ScopeId, freshScope.ScopeId]);
        outer.AttemptNumber.Should().Be(1, "the inner command's failure was retried by its own attempts, not the outer's");
    }

    [TestMethod]
    public async Task Without_a_policy_the_handler_sees_the_callers_scope()
    {
        // Arrange
        await using var host = await StartHostAsync(withPolicy: false);
        var mediator = host.GetRequiredService<IMediator>();
        var probe = host.GetRequiredService<RetryProbe>();
        var callersScope = host.GetRequiredService<AttemptProbe>().Id;

        // Act
        var result = await mediator.ExecuteAsync(new FlakyCommand(Failures: 0));

        // Assert -- no attempt, no new scope: the behavior and the handler resolve from the caller's provider
        result.Should().Be(callersScope);
        var handled = probe.At(nameof(FlakyCommand)).Should().ContainSingle().Subject;
        handled.ScopeId.Should().Be(callersScope);
        handled.AttemptNumber.Should().BeNull();
        probe.At(BehaviorSite(nameof(FlakyCommand))).Should().ContainSingle()
            .Which.ScopeId.Should().Be(callersScope);
    }

    [TestMethod]
    public async Task A_FireAndForget_local_command_creates_no_attempt_scope_and_is_enqueued_once()
    {
        // Arrange -- the handler fails retryably, which would be retried if it ran under an attempt
        await using var host = await StartHostAsync(withPolicy: true);
        var mediator = host.GetRequiredService<IMediator>();
        var probe = host.GetRequiredService<RetryProbe>();
        var callersScope = host.GetRequiredService<AttemptProbe>().Id;

        // Act
        await mediator.ExecuteAsync(new FlakyFireAndForgetCommand(Failures: 1));
        await probe.WaitForAsync(nameof(FlakyFireAndForgetCommand), 1, WaitTimeout);

        // Assert -- the pipeline ran around the enqueue on the caller's provider, outside any attempt
        var behaved = probe.At(BehaviorSite(nameof(FlakyFireAndForgetCommand))).Should().ContainSingle().Subject;
        behaved.ScopeId.Should().Be(callersScope);
        behaved.AttemptNumber.Should().BeNull();

        // Assert -- the handler ran once, in the background dispatcher's scope, not as an attempt
        var handled = probe.At(nameof(FlakyFireAndForgetCommand)).Should().ContainSingle().Subject;
        handled.AttemptNumber.Should().BeNull();
        handled.ScopeId.Should().NotBe(callersScope);
    }

    [TestMethod]
    public async Task Exhaustion_rethrows_the_last_exception_to_the_caller()
    {
        // Arrange
        await using var host = await StartHostAsync(withPolicy: true);
        var mediator = host.GetRequiredService<IMediator>();
        var probe = host.GetRequiredService<RetryProbe>();

        // Act
        var act = () => mediator.ExecuteAsync(new FlakyCommand(Failures: 5));

        // Assert -- three attempts (the default), and the caller sees the third one's exception
        await act.Should().ThrowAsync<RetryMeException>().WithMessage("FlakyCommand failed on call 3");
        probe.At(nameof(FlakyCommand)).Select(sighting => sighting.AttemptNumber).Should().Equal(1, 2, 3);
    }

    [TestMethod]
    public async Task MessageId_is_the_same_for_every_attempt()
    {
        // Arrange
        await using var host = await StartHostAsync(withPolicy: true);
        var mediator = host.GetRequiredService<IMediator>();
        var probe = host.GetRequiredService<RetryProbe>();

        // Act
        await mediator.ExecuteAsync(new FlakyCommand(Failures: 1));

        // Assert -- the context is started once, outside the attempts
        var handled = probe.At(nameof(FlakyCommand));
        handled.Should().HaveCount(2);
        handled[0].MessageId.Should().NotBeNullOrEmpty();
        handled[1].MessageId.Should().Be(handled[0].MessageId);
    }

    /// <summary>Where <see cref="RetryProbeBehavior{TRequest, TResult}"/> records a dispatch of the named request.</summary>
    private static string BehaviorSite(string requestName) => requestName + ".behavior";

    private static Task<TestHost> StartHostAsync(bool withPolicy)
        => TestHost.StartAsync(builder =>
        {
            builder.Services.AddSingleton<RetryProbe>();
            builder.Services.AddScoped<AttemptProbe>();
            builder.Services.AddConduit(conduit =>
            {
                conduit.AddPipeline(typeof(RetryProbeBehavior<,>));
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
