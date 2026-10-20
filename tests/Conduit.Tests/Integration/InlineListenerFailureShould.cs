using AwesomeAssertions;
using Conduit.Exceptions;
using Conduit.Generated;
using Conduit.Runtime;
using Conduit.TestUtils;
using Conduit.Tests.Support;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Conduit.Tests.Integration;

[TestClass]
public class InlineListenerFailureShould
{
    [TestMethod]
    public async Task ThrowEventListenerExceptionToThePublisher()
    {
        // Arrange
        await using var host = TestWebHost.Start(builder =>
        {
            builder.Services.AddSingleton<EventSpy<string>>();
            builder.Services.AddConduit();
        });
        var mediator = host.GetRequiredService<IMediator>();

        // Act
        var act = async () => await mediator.PublishAsync(new InlineFailureEvent("x"));

        // Assert
        var thrown = (await act.Should().ThrowAsync<EventListenerException>()).Which;
        thrown.ListenerExceptions.Should().HaveCount(2);
    }

    [TestMethod]
    public async Task RunEveryListenerBeforeThrowing()
    {
        // Arrange
        await using var host = TestWebHost.Start(builder =>
        {
            builder.Services.AddSingleton<EventSpy<string>>();
            builder.Services.AddConduit();
        });
        var mediator = host.GetRequiredService<IMediator>();
        var runs = host.GetRequiredService<EventSpy<string>>();

        // Act
        var act = async () => await mediator.PublishAsync(new InlineFailureEvent("x"));
        await act.Should().ThrowAsync<EventListenerException>();

        // Assert
        runs.Events.Should().BeEquivalentTo(
            [nameof(InlineFailingListenerA), nameof(InlineFailingListenerB), nameof(InlinePassingListener)]);
    }

    [TestMethod]
    public async Task ChainTheFirstListenerFailureAsInnerException()
    {
        // Arrange
        await using var host = TestWebHost.Start(builder =>
        {
            builder.Services.AddSingleton<EventSpy<string>>();
            builder.Services.AddConduit();
        });
        var mediator = host.GetRequiredService<IMediator>();
        var runs = host.GetRequiredService<EventSpy<string>>();

        // Act
        var act = async () => await mediator.PublishAsync(new InlineFailureEvent("x"));
        var thrown = (await act.Should().ThrowAsync<EventListenerException>()).Which;

        // Assert — the first failure in run order is the inner exception
        thrown.InnerException.Should().BeSameAs(thrown.ListenerExceptions[0]);
        var firstFailingListener = runs.Events.First(name => name != nameof(InlinePassingListener));
        var expectedType = firstFailingListener == nameof(InlineFailingListenerA)
            ? typeof(InvalidOperationException)
            : typeof(ArgumentException);
        thrown.InnerException.Should().BeOfType(expectedType);
    }

    [TestMethod]
    public async Task ReachTheRootCauseThroughGetBaseException()
    {
        // Arrange — drop B so A (whose cause is an InlineRootCauseException) is the only failure
        await using var host = TestWebHost.Start(builder =>
        {
            builder.Services.AddSingleton<EventSpy<string>>();
            builder.Services.AddConduit();
            builder.Services.RemoveAll<Conduit.Handlers.IEventListener<InlineFailureEvent>>();
            builder.Services.AddTransient<Conduit.Handlers.IEventListener<InlineFailureEvent>, InlineFailingListenerA>();
        });
        var mediator = host.GetRequiredService<IMediator>();

        // Act
        var act = async () => await mediator.PublishAsync(new InlineFailureEvent("x"));
        var thrown = (await act.Should().ThrowAsync<EventListenerException>()).Which;

        // Assert
        thrown.GetBaseException().Should().BeOfType<InlineRootCauseException>()
            .Which.Message.Should().Be("root of A");
    }

    [TestMethod]
    public async Task SkipTheTransportBroadcastWhenAnInlineListenerFails()
    {
        // Arrange
        var transport = new SpyTransport();
        await using var host = TestWebHost.Start(builder =>
        {
            builder.Services.AddSingleton<EventSpy<string>>();
            builder.Services.AddSingleton<IConduitTransport>(transport);
            builder.Services.AddConduit();
        });
        var mediator = host.GetRequiredService<IMediator>();

        // Act
        var act = async () => await mediator.PublishAsync(new InlineFailureEvent("x"));
        await act.Should().ThrowAsync<EventListenerException>();

        // Assert -- the publish failed, so its event is never announced
        transport.SentEvents.Should().BeEmpty();
    }

    [TestMethod]
    public async Task BroadcastTheSameEventWhenEveryInlineListenerPasses()
    {
        // Arrange -- the control: only the passing listener, so the broadcast above is skipped by the failure alone
        var transport = new SpyTransport();
        await using var host = TestWebHost.Start(builder =>
        {
            builder.Services.AddSingleton<EventSpy<string>>();
            builder.Services.AddSingleton<IConduitTransport>(transport);
            builder.Services.AddConduit();
            builder.Services.RemoveAll<Conduit.Handlers.IEventListener<InlineFailureEvent>>();
            builder.Services.AddTransient<Conduit.Handlers.IEventListener<InlineFailureEvent>, InlinePassingListener>();
        });
        var mediator = host.GetRequiredService<IMediator>();

        // Act
        await mediator.PublishAsync(new InlineFailureEvent("x"));

        // Assert
        transport.SentEvents.Should().ContainSingle(envelope => envelope.Type == typeof(InlineFailureEvent).FullName);
    }

    [TestMethod]
    public async Task NotThrowToThePublisherWhenAFireAndForgetListenerFails()
    {
        // Arrange
        await using var host = TestWebHost.Start(builder =>
        {
            builder.Services.AddSingleton<EventSpy<FailingFireAndForgetEvent>>();
            builder.Services.AddConduit();
        });
        var mediator = host.GetRequiredService<IMediator>();
        var spy = host.GetRequiredService<EventSpy<FailingFireAndForgetEvent>>();

        // Act
        var act = async () => await mediator.PublishAsync(new FailingFireAndForgetEvent("x"));

        // Assert — the publish succeeds; the listener still ran (in the background) and its failure was logged
        await act.Should().NotThrowAsync();
        await spy.WaitForCountAsync(1);
    }
}
