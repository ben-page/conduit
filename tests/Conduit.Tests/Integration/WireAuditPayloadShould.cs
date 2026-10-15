using System.Text.Json;
using AwesomeAssertions;
using Conduit.Generated;
using Conduit.Runtime;
using Conduit.Tests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace Conduit.Tests.Integration;

/// <summary>
/// The generated wire dispatch records each request's contents, redacted exactly as the in-process path redacts them
/// (sensitive-value reveal spec §3.2). The observer reads the payload at close, where DispatchGroup seals it.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class WireAuditPayloadShould
{
    private ServiceProvider? serviceProvider;
    private ClosingPayloadObserver observer = null!;

    [TestInitialize]
    public void Setup()
    {
        this.observer = new ClosingPayloadObserver();
        this.serviceProvider = BuildProvider(this.observer, configure: null);
    }

    [TestCleanup]
    public void Cleanup()
    {
        MessageContext.RestoreContext(null);
        this.serviceProvider?.Dispose();
    }

    private static ServiceProvider BuildProvider(IConduitObserver observer, Action<Conduit.Configuration.ConduitBuilder>? configure)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(observer);
        services.AddConduit(conduit => configure?.Invoke(conduit));
        return services.BuildServiceProvider();
    }

    private async Task<DispatchResult<object>> SendAsync(string type, object payload)
    {
        var wireMediator = this.serviceProvider!.GetRequiredService<IWireMediator>();
        MessageContext.StartDispatchFromTransport(Guid.NewGuid().ToString(), null, Guid.NewGuid().ToString());
        return await wireMediator.HandleWireRequestAsync(
            new DispatchEnvelope(type, JsonSerializer.SerializeToElement(payload)), CancellationToken.None);
    }

    [TestMethod]
    public async Task CaptureAWireCommandsPayload()
    {
        // Act
        var result = await this.SendAsync(typeof(EchoCommand).FullName!, new { value = "wire-echo" });

        // Assert
        result.Error.Should().BeNull();
        this.observer.ClosedOfType(typeof(EchoCommand).FullName!).PayloadJson.Should().Contain("wire-echo");
    }

    [TestMethod]
    public async Task CaptureAWireQuerysPayload()
    {
        // Act
        var result = await this.SendAsync(typeof(SimpleQuery).FullName!, new { value = "wire-query" });

        // Assert
        result.Error.Should().BeNull();
        this.observer.ClosedOfType(typeof(SimpleQuery).FullName!).PayloadJson.Should().Contain("wire-query");
    }

    [TestMethod]
    public async Task CaptureAWireEventsPayload()
    {
        // Arrange
        var wireMediator = this.serviceProvider!.GetRequiredService<IWireMediator>();

        MessageContext.StartDispatchFromTransport(Guid.NewGuid().ToString(), null, Guid.NewGuid().ToString());

        // Act
        await wireMediator.HandleWireEventAsync(
            new DispatchEnvelope(typeof(SimpleEvent).FullName!, JsonSerializer.SerializeToElement(new { data = "wire-event" })),
            CancellationToken.None);

        // Assert
        this.observer.ClosedOfType(typeof(SimpleEvent).FullName!).PayloadJson.Should().Contain("wire-event");
    }

    [TestMethod]
    public async Task DropUnknownAndDoNotAuditMembersFromAWireRequest()
    {
        // Act -- the client sends a property the contract does not have, plus the [DoNotAudit] member
        var result = await this.SendAsync(
            typeof(SecretBearingCommand).FullName!, new { name = "visible", secret = "hunter2", injected = "evil" });

        // Assert -- the stored body is the TYPED object re-serialized, never the raw client JSON
        result.Error.Should().BeNull();
        var payload = this.observer.ClosedOfType(typeof(SecretBearingCommand).FullName!).PayloadJson;
        payload.Should().Contain("visible");
        payload.Should().NotContain("hunter2").And.NotContainEquivalentOf("secret");
        payload.Should().NotContain("evil").And.NotContainEquivalentOf("injected");
    }

    [TestMethod]
    public async Task DropANestedDoNotAuditMemberFromAWireRequest()
    {
        // Act
        var result = await this.SendAsync(
            typeof(NestedSecretCommand).FullName!,
            new { lines = new[] { new { label = "first-line", pin = "9999" } } });

        // Assert
        result.Error.Should().BeNull();
        var payload = this.observer.ClosedOfType(typeof(NestedSecretCommand).FullName!).PayloadJson;
        payload.Should().Contain("first-line");
        payload.Should().NotContain("9999");
    }

    [TestMethod]
    public async Task CaptureNothingForATypeLevelDoNotAuditRequest()
    {
        // Act
        var result = await this.SendAsync(typeof(WholeSecretCommand).FullName!, new { secret = "whole" });

        // Assert
        result.Error.Should().BeNull();
        this.observer.ClosedOfType(typeof(WholeSecretCommand).FullName!).PayloadJson.Should().BeNull();
    }

    [TestMethod]
    public async Task RecordAFailedRowWithoutAPayloadForUnreadableJson()
    {
        // Act -- `name` must be a string; the read throws before anything is captured
        var result = await this.SendAsync(typeof(SecretBearingCommand).FullName!, new { name = 5, secret = "s" });

        // Assert -- the dispatch still closes (failed), so the audit trail keeps the probe
        result.Error.Should().NotBeNull();
        var closed = this.observer.ClosedOfType(typeof(SecretBearingCommand).FullName!);
        closed.Failed.Should().BeTrue();
        closed.PayloadJson.Should().BeNull();
    }

    [TestMethod]
    public async Task KeepThePayloadOnAFailedHandler()
    {
        // Act -- FailingCommandHandler throws after the arm captured the payload
        var result = await this.SendAsync(typeof(FailingCommand).FullName!, new { reason = "handler-blew-up" });

        // Assert
        result.Error.Should().NotBeNull();
        var closed = this.observer.ClosedOfType(typeof(FailingCommand).FullName!);
        closed.Failed.Should().BeTrue();
        closed.PayloadJson.Should().Contain("handler-blew-up");
    }

    [TestMethod]
    public async Task KeepConcurrentWirePayloadsApart()
    {
        // Arrange
        var wireMediator = this.serviceProvider!.GetRequiredService<IWireMediator>();
        Task<DispatchResult<object>> Send(string value) => Task.Run(() =>
        {
            MessageContext.StartDispatchFromTransport(Guid.NewGuid().ToString(), null, Guid.NewGuid().ToString());
            return wireMediator.HandleWireRequestAsync(
                new DispatchEnvelope(typeof(EchoCommand).FullName!, JsonSerializer.SerializeToElement(new { value })),
                CancellationToken.None);
        });

        // Act
        await Task.WhenAll(Enumerable.Range(0, 20).Select(i => Send($"concurrent-{i}")));

        // Assert -- every record carries exactly its own value
        var closed = this.observer.Closed.Where(c => c.MessageType == typeof(EchoCommand).FullName!).ToList();
        closed.Should().HaveCount(20);
        closed.Select(c => c.PayloadJson).Should().OnlyContain(p => p != null && p.Contains("concurrent-"));
        closed.Select(c => JsonDocument.Parse(c.PayloadJson!).RootElement.GetProperty("value").GetString())
            .Should().BeEquivalentTo(Enumerable.Range(0, 20).Select(i => $"concurrent-{i}"));
    }

    [TestMethod]
    public async Task RecordNoPayloadForARouteForwardedRequest()
    {
        // Arrange -- route the support namespace to a spy transport, so EchoCommand is forwarded unread
        await this.serviceProvider!.DisposeAsync();
        var spyTransport = new SpyTransport { RequestResult = "forwarded" };
        this.serviceProvider = BuildProvider(this.observer, conduit =>
            conduit.AddEndpoint("remote", endpoint =>
            {
                endpoint.UseTransport(spyTransport);
                endpoint.RouteNamespace<EchoCommand>();
            }));

        // Act
        var result = await this.SendAsync(typeof(EchoCommand).FullName!, new { value = "forward-me" });

        // Assert
        result.Error.Should().BeNull();
        spyTransport.SentRequests.Should().ContainSingle();
        this.observer.ClosedOfType(typeof(EchoCommand).FullName!).PayloadJson.Should().BeNull(
            "the route-forward branch never deserializes, and raw client JSON would skip redaction");
    }
}
