using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Text.Json;
using AwesomeAssertions;
using Conduit.Generated;
using Conduit.Runtime;
using Conduit.Tests.Support;
using Conduit.Tests.Support.ForwardedMetrics;
using Microsoft.Extensions.DependencyInjection;

namespace Conduit.Tests.Integration;

/// <summary>
/// Every dispatch the generated mediator observes records <c>conduit.dispatch.count</c> and
/// <c>conduit.dispatch.duration</c> once, tagged with its type, its kind and its outcome -- in process and from the wire.
/// </summary>
[TestClass]
public sealed class DispatchMetricsShould
{
    [TestCleanup]
    public void Cleanup() => MessageContext.RestoreContext(null);

    [TestMethod]
    public async Task RecordAnInProcessCommandOnceAsASuccess()
    {
        // Arrange
        using var metrics = new DispatchMetrics(typeof(MeteredCommand).FullName!);
        await using var provider = BuildProvider();

        // Act
        var result = await provider.GetRequiredService<IMediator>().ExecuteAsync(new MeteredCommand("metered"));

        // Assert
        result.Should().Be("metered");
        metrics.Counts.Should().ContainSingle().Which.Should().Be(new Measurement(1, "command", "success"));
        metrics.Durations.Should().ContainSingle().Which.Kind.Should().Be("command");
        metrics.Durations.Single().Value.Should().BeGreaterThanOrEqualTo(0);
    }

    [TestMethod]
    public async Task RecordADispatchThatThrowsAsAnError()
    {
        // Arrange
        using var metrics = new DispatchMetrics(typeof(MeteredFailingCommand).FullName!);
        await using var provider = BuildProvider();

        // Act
        var act = () => provider.GetRequiredService<IMediator>().ExecuteAsync(new MeteredFailingCommand());

        // Assert
        await act.Should().ThrowAsync<InvalidOperationException>();
        metrics.Counts.Should().ContainSingle().Which.Should().Be(new Measurement(1, "command", "error"));
        metrics.Durations.Should().ContainSingle().Which.Outcome.Should().Be("error");
    }

    [TestMethod]
    public async Task RecordAWireQueryUnderItsDeclaredKind()
    {
        // Arrange -- the wire carries only the type name
        using var metrics = new DispatchMetrics(typeof(MeteredWireQuery).FullName!);
        await using var provider = BuildProvider();
        var envelope = new DispatchEnvelope(
            typeof(MeteredWireQuery).FullName!, JsonSerializer.SerializeToElement(new { id = 7 }));

        // Act
        var result = await provider.GetRequiredService<IWireMediator>().HandleWireRequestAsync(envelope, CancellationToken.None);

        // Assert
        result.Error.Should().BeNull();
        metrics.Counts.Should().ContainSingle().Which.Should().Be(new Measurement(1, "query", "success"));
        metrics.Durations.Should().ContainSingle().Which.Kind.Should().Be("query");
    }

    [TestMethod]
    public async Task RecordAForwardedRequestOfAnUndeclaredTypeUnderNoNameOfTheCallers()
    {
        // Arrange -- the namespace is routed away and holds no message this compilation declares
        var forwardedType = typeof(ForwardedMetricsNamespace).Namespace + ".UndeclaredRequest";
        using var named = new DispatchMetrics(forwardedType);
        using var metrics = new DispatchMetrics(Undeclared);
        var spyTransport = new SpyTransport { RequestResult = "forwarded" };
        await using var provider = BuildProvider(conduit => conduit.AddEndpoint("remote", endpoint =>
        {
            endpoint.UseTransport(spyTransport);
            endpoint.RouteNamespace<ForwardedMetricsNamespace>();
        }));
        var envelope = new DispatchEnvelope(forwardedType, JsonSerializer.SerializeToElement(new { }));

        // Act
        var result = await provider.GetRequiredService<IWireMediator>().HandleWireRequestAsync(envelope, CancellationToken.None);

        // Assert
        result.Error.Should().BeNull();
        spyTransport.SentRequests.Should().ContainSingle();
        named.Counts.Should().BeEmpty("the caller chose the name");
        // Contain, not ContainSingle: another test may dispatch an undeclared type at the same time
        metrics.Counts.Should().Contain(new Measurement(1, "request", "success"));
    }

    [TestMethod]
    public async Task RecordAWireRequestNothingDeclaresAsAnUndeclaredError()
    {
        // Arrange -- no route, no handler: the dispatch fails on the unknown name
        var madeUpType = "Made.Up." + Guid.NewGuid().ToString("N");
        using var named = new DispatchMetrics(madeUpType);
        using var metrics = new DispatchMetrics(Undeclared);
        await using var provider = BuildProvider();
        var envelope = new DispatchEnvelope(madeUpType, JsonSerializer.SerializeToElement(new { }));

        // Act
        var result = await provider.GetRequiredService<IWireMediator>().HandleWireRequestAsync(envelope, CancellationToken.None);

        // Assert
        result.Error.Should().NotBeNull();
        named.Counts.Should().BeEmpty("a made-up name must not become a series");
        metrics.Counts.Should().Contain(new Measurement(1, "request", "error"));
    }

    [TestMethod]
    public async Task RecordAnInProcessEventOnce()
    {
        // Arrange
        using var metrics = new DispatchMetrics(typeof(MeteredEvent).FullName!);
        await using var provider = BuildProvider();

        // Act
        await provider.GetRequiredService<IMediator>().PublishAsync(new MeteredEvent());

        // Assert
        metrics.Counts.Should().ContainSingle().Which.Should().Be(new Measurement(1, "event", "success"));
        metrics.Durations.Should().ContainSingle().Which.Kind.Should().Be("event");
    }

    [TestMethod]
    public async Task RecordAWireEventOnce()
    {
        // Arrange
        using var metrics = new DispatchMetrics(typeof(MeteredWireEvent).FullName!);
        await using var provider = BuildProvider();
        var envelope = new DispatchEnvelope(typeof(MeteredWireEvent).FullName!, JsonSerializer.SerializeToElement(new { }));

        // Act
        await provider.GetRequiredService<IWireMediator>().HandleWireEventAsync(envelope, CancellationToken.None);

        // Assert
        metrics.Counts.Should().ContainSingle().Which.Should().Be(new Measurement(1, "event", "success"));
        metrics.Durations.Should().ContainSingle().Which.Kind.Should().Be("event");
    }

    private const string Undeclared = "(undeclared)";

    private static ServiceProvider BuildProvider(Action<Conduit.Configuration.ConduitBuilder>? configure = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddConduit(conduit => configure?.Invoke(conduit));
        return services.BuildServiceProvider();
    }

    private sealed record Measurement(double Value, string? Kind, string? Outcome);

    /// <summary>
    /// Listens to the dispatch count and duration, keeping only the measurements of the one type it is given: the meter is
    /// static and shared by parallel tests.
    /// </summary>
    private sealed class DispatchMetrics : IDisposable
    {
        private readonly MeterListener listener = new();
        private readonly string ownType;
        private readonly ConcurrentQueue<Measurement> counts = new();
        private readonly ConcurrentQueue<Measurement> durations = new();

        public DispatchMetrics(string ownType)
        {
            this.ownType = ownType;
            this.listener.InstrumentPublished = (instrument, meterListener) =>
            {
                if (instrument.Meter.Name == "Conduit" && instrument.Name is "conduit.dispatch.count" or "conduit.dispatch.duration")
                {
                    meterListener.EnableMeasurementEvents(instrument);
                }
            };
            this.listener.SetMeasurementEventCallback<long>((_, value, tags, _) => this.Keep(this.counts, value, tags));
            this.listener.SetMeasurementEventCallback<double>((_, value, tags, _) => this.Keep(this.durations, value, tags));
            this.listener.Start();
        }

        public IReadOnlyCollection<Measurement> Counts => this.counts;

        public IReadOnlyCollection<Measurement> Durations => this.durations;

        public void Dispose() => this.listener.Dispose();

        private void Keep(ConcurrentQueue<Measurement> into, double value, ReadOnlySpan<KeyValuePair<string, object?>> tags)
        {
            var tagValues = new Dictionary<string, object?>();
            foreach (var tag in tags)
            {
                tagValues[tag.Key] = tag.Value;
            }

            if ((tagValues.GetValueOrDefault("conduit.message.type") as string) == this.ownType)
            {
                into.Enqueue(new Measurement(
                    value,
                    tagValues.GetValueOrDefault("conduit.message.kind") as string,
                    tagValues.GetValueOrDefault("conduit.outcome") as string));
            }
        }
    }
}
