using System.Collections.Concurrent;
using System.Diagnostics;
using AwesomeAssertions;
using Conduit.Runtime;
using Conduit.TestUtils;
using Conduit.Transport.AzureServiceBus.Tests.Support.Contracts;
using Microsoft.Extensions.DependencyInjection;

namespace Conduit.Transport.AzureServiceBus.Tests.Integration;

/// <summary>
/// Confirms against a real Azure Service Bus emulator what Tasks 2-4 proved only against the
/// in-memory transport double: a self-published event runs local listeners exactly once, and
/// correlation plus W3C trace context assembled from ambient state at publish time survives the
/// wire.
/// </summary>
[TestClass]
public class AsbSelfPublishedEventShould : AsbTestBase
{
    [TestMethod]
    public async Task RunLocalListenersOnceForASelfPublishedEvent()
    {
        // Arrange — a single host that both publishes TestEvent and has a local TestEventListener
        // for it. Every discovered event type gets topology for every host (not just the ones it
        // happens to publish), so this app's own subscription is bound to TestEvent's topic and
        // its own broadcast copy routes straight back to it.
        var appName = UniqueAppName("self-pub");

        await using var host = await CreateHostAsync(appName);
        var spy = host.GetRequiredService<SpyLog>();
        var mediator = host.GetRequiredService<IMediator>();

        // Act
        await mediator.PublishAsync(new TestEvent("self-published"));

        // Assert — the local listener running once at publish time is not in question; what's
        // under test is whether it runs a SECOND time once the broker routes this app's own
        // broadcast copy back to it. A bare await on PublishAsync returns long before that round
        // trip completes and would pass vacuously whether or not the fix is present, so wait
        // deliberately for longer than the publish-broker-redeliver round trip takes before
        // asserting the count stays at exactly one.
        await Task.Delay(TimeSpan.FromSeconds(2));
        spy.Count.Should().Be(1);
        spy.Invocations.OfType<TestEvent>().Should().ContainSingle(e => e.Value == "self-published");
    }

    private static ActivitySamplingResult SampleAllData(
        ref ActivityCreationOptions<ActivityContext> _) =>
        ActivitySamplingResult.AllDataAndRecorded;

    [TestMethod]
    public async Task CarryKnownCorrelationAndTraceContextAcrossTheWire()
    {
        // Arrange — scope Activity capture to this test's own trace via a locally-started root
        // activity. Even though the ASB assembly disables class-level parallelism
        // ([assembly: DoNotParallelize] in AssemblySetup.cs, for the emulator's 50-entity cap),
        // scoping to this test's own trace keeps the assertion correct regardless — the same
        // isolation trick used in BrokerOutboundHeadersShould / TracingIntegrationShould in
        // Conduit.Tests.
        using var testRoot = new Activity("AsbSelfPublishedEventShould.Scope").Start();
        var rootTraceId = testRoot.TraceId;
        var capturedActivities = new ConcurrentBag<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = static source => source.Name == "Conduit",
            Sample = SampleAllData,
            ActivityStarted = activity =>
            {
                if (activity.TraceId == rootTraceId)
                {
                    capturedActivities.Add(activity);
                }
            },
        };
        ActivitySource.AddActivityListener(listener);

        var handlerName = UniqueAppName("handler");
        var publisherName = UniqueAppName("pub");

        // Command/query handlers are cleared on both hosts because the transport enforces
        // unicast subscriptions for commands/queries (one app per type); leaving handlers
        // registered in two apps would cause a subscription forward-target collision.
        await using var handlerHost = await CreateHostAsync(handlerName, configureHandling: handling =>
        {
            handling.ClearCommandHandlers();
            handling.ClearQueryHandlers();
        });
        var prefix = handlerHost.TopologyPrefix!;
        await using var publisherHost = await CreateHostAsync(publisherName, prefix, handling =>
        {
            handling.ClearCommandHandlers();
            handling.ClearQueryHandlers();
        });

        var handlerSpy = handlerHost.GetRequiredService<SpyLog>();
        var mediator = publisherHost.GetRequiredService<IMediator>();

        // Act — establish a two-level known MessageContext before publishing: an outer
        // "conversation root" context, then an inner context nested under it. This makes
        // CausationId and ConversationId on the outbound event genuinely distinct known values
        // (both would otherwise collapse to the same head id), so the assertions below cannot
        // pass by the two fields merely echoing one another. This mirrors
        // BrokerOutboundHeadersShould.CarryTheCorrelationIdsOfTheDispatchThatPublished, but proves
        // the same thing end-to-end over a real broker rather than against the in-memory double —
        // the regression guard for BuildOutboundHeaders reading MessageContext.Current per call
        // instead of the old thread-affine [ThreadStatic] state.
        MessageContext.StartDispatch();
        var conversationRootId = MessageContext.Current!.MessageId;
        MessageContext.StartDispatch();
        var immediateCauseId = MessageContext.Current!.MessageId;
        await mediator.PublishAsync(new ContextCaptureEvent("known-context"));

        // Assert — MessageContext ids survived the wire intact, not just non-empty
        await handlerSpy.WaitForCountAsync(1);
        var snapshot = handlerSpy.Invocations.OfType<MessageContextSnapshot>().First();
        snapshot.CausationId.Should().Be(immediateCauseId);
        snapshot.ConversationId.Should().Be(conversationRootId);
        snapshot.MessageId.Should().NotBeNullOrWhiteSpace();
        snapshot.MessageId.Should().NotBe(immediateCauseId);

        // Assert — the W3C trace context (traceparent) also survived the wire. The consumer-side
        // activity that wrapped the inbound handling is a genuine descendant of this test's root
        // activity, not an unrelated freshly-rooted trace — which is what would happen if
        // BuildOutboundHeaders failed to inject the current Activity into the outbound headers.
        var consumerActivity = capturedActivities.FirstOrDefault(activity =>
            activity.Kind == ActivityKind.Consumer
            && (activity.GetTagItem("conduit.message.type") as string) == typeof(ContextCaptureEvent).FullName);
        consumerActivity.Should().NotBeNull(
            "the extracted traceparent header should have linked the consumer activity into this trace");
    }
}
