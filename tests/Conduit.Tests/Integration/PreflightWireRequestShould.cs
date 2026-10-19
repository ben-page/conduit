using System.Text.Json;
using AwesomeAssertions;
using Conduit.Configuration;
using Conduit.Generated;
using Conduit.Middleware;
using Conduit.Runtime;
using Conduit.TestUtils;
using Conduit.Tests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace Conduit.Tests.Integration;

/// <summary>
/// <see cref="ConduitRuntime.PreflightWireRequestAsync"/>: a wire request's inbound checks -- the gates, the read, and the
/// pipeline's <see cref="IInboundCheckBehavior"/>s in its order -- without its handler, answering as the wire answers. Each
/// host's pipeline is a check, a behavior that is not one, and a second check; both contracts' handlers throw if called.
/// </summary>
[TestClass]
public class PreflightWireRequestShould
{
    [TestMethod]
    public async Task A_server_only_contract_is_refused_as_the_wire_refuses_it()
    {
        // Arrange -- the [ClientVisible] type filter lets only the void command through
        await using var host = await StartHostAsync(conduit =>
            conduit.AddTypeFilter(filter => filter.AllowType(typeof(PreflightVoidCommand).FullName!)));
        var payload = Payload(new PreflightCommand("admit"));

        // Act
        var preflight = await PreflightAsync(host, typeof(PreflightCommand).FullName!, payload);
        var wire = await WireAsync(host, typeof(PreflightCommand).FullName!, payload);

        // Assert
        preflight.Admitted.Should().BeFalse();
        preflight.Refusal!.Code.Should().Be("Forbidden");
        ShouldBeTheSameAnswer(preflight.Refusal, wire.Error);
        Probe(host).Sites.Should().BeEmpty("a gate refuses before the payload is read");
    }

    [TestMethod]
    public async Task A_check_behaviors_refusal_comes_back_unchanged()
    {
        // Arrange
        await using var host = await StartHostAsync();
        var payload = Payload(new PreflightCommand("refuse"));

        // Act
        var preflight = await PreflightAsync(host, typeof(PreflightCommand).FullName!, payload);
        var wire = await WireAsync(host, typeof(PreflightCommand).FullName!, payload);

        // Assert -- the typed refusal's code, status, type and data, as the wire carries them
        preflight.Admitted.Should().BeFalse();
        preflight.Refusal!.Code.Should().Be("Conflict");
        preflight.Refusal.Status.Should().Be(409);
        preflight.Refusal.Type.Should().Be(typeof(PreflightRefusal).FullName);
        preflight.Refusal.Data!.Value.GetProperty("reason").GetString().Should().Be("locked");
        ShouldBeTheSameAnswer(preflight.Refusal, wire.Error);
    }

    [TestMethod]
    public async Task An_admitted_request_runs_its_checks_in_the_pipelines_order_and_never_its_handler()
    {
        // Arrange
        await using var host = await StartHostAsync();
        var payload = Payload(new PreflightCommand("admit"));

        // Act
        var preflight = await PreflightAsync(host, typeof(PreflightCommand).FullName!, payload);
        var checkSites = Probe(host).Sites;
        var wire = await WireAsync(host, typeof(PreflightCommand).FullName!, payload);

        // Assert -- the checks ran in order, the other behavior never; the wire reaches the handler, which throws
        preflight.Should().Be(new WirePreflightResult(Admitted: true, Refusal: null));
        checkSites.Should().Equal("check", "second check");
        wire.Error!.Code.Should().Be("InternalError", "the handler throws when it is reached");
    }

    [TestMethod]
    public async Task A_void_command_is_admitted_through_its_checks()
    {
        // Arrange
        await using var host = await StartHostAsync();

        // Act
        var preflight = await PreflightAsync(host, typeof(PreflightVoidCommand).FullName!, Payload(new PreflightVoidCommand()));

        // Assert
        preflight.Should().Be(new WirePreflightResult(Admitted: true, Refusal: null));
        Probe(host).Sites.Should().Equal("check", "second check");
    }

    [TestMethod]
    public async Task An_unexpected_exception_comes_back_as_the_wire_maps_it()
    {
        // Arrange
        await using var host = await StartHostAsync();
        var payload = Payload(new PreflightCommand("explode"));

        // Act
        var preflight = await PreflightAsync(host, typeof(PreflightCommand).FullName!, payload);
        var wire = await WireAsync(host, typeof(PreflightCommand).FullName!, payload);

        // Assert -- InternalError with a support reference, and nothing of the exception's text
        preflight.Admitted.Should().BeFalse();
        preflight.Refusal!.Code.Should().Be("InternalError");
        preflight.Refusal.Status.Should().Be(500);
        preflight.Refusal.Message.Should().Be("An unexpected error occurred.").And.Be(wire.Error!.Message);
        preflight.Refusal.Reference.Should().MatchRegex("^[A-Za-z0-9-]{1,64}$");
        preflight.Refusal.Type.Should().BeNull();
        preflight.Refusal.Data.Should().BeNull();
    }

    [TestMethod]
    public async Task An_exception_a_translator_claims_comes_back_as_its_refusal()
    {
        // Arrange
        await using var host = await StartHostAsync(
            configureServices: services => services.AddSingleton<IWireExceptionTranslator, PreflightTranslator>());
        var payload = Payload(new PreflightCommand("translate"));

        // Act
        var preflight = await PreflightAsync(host, typeof(PreflightCommand).FullName!, payload);
        var wire = await WireAsync(host, typeof(PreflightCommand).FullName!, payload);

        // Assert
        preflight.Refusal!.Code.Should().Be("PreconditionFailed");
        ShouldBeTheSameAnswer(preflight.Refusal, wire.Error);
    }

    [TestMethod]
    public async Task An_unreadable_payload_is_refused_as_the_wire_refuses_it()
    {
        // Arrange -- the payload omits the contract's constructor parameter
        await using var host = await StartHostAsync();
        var payload = JsonSerializer.SerializeToElement(new { });

        // Act
        var preflight = await PreflightAsync(host, typeof(PreflightCommand).FullName!, payload);
        var wire = await WireAsync(host, typeof(PreflightCommand).FullName!, payload);

        // Assert
        preflight.Admitted.Should().BeFalse();
        preflight.Refusal!.Code.Should().Be("ContractMismatch");
        ShouldBeTheSameAnswer(preflight.Refusal, wire.Error);
        Probe(host).Sites.Should().BeEmpty();
    }

    [TestMethod]
    public async Task A_request_a_gate_discards_is_neither_admitted_nor_refused()
    {
        // Arrange
        await using var host = await StartHostAsync(
            configureServices: services => services.AddSingleton<IInboundInterceptor, PreflightDiscardingGate>());
        var payload = Payload(new PreflightCommand("admit"));

        // Act
        var preflight = await PreflightAsync(host, typeof(PreflightCommand).FullName!, payload);
        var wire = await WireAsync(host, typeof(PreflightCommand).FullName!, payload);

        // Assert -- the wire answers a discard with no error and no data
        preflight.Should().Be(new WirePreflightResult(Admitted: false, Refusal: null));
        wire.Error.Should().BeNull();
        wire.Data.Should().BeNull();
        Probe(host).Sites.Should().BeEmpty();
    }

    [TestMethod]
    public async Task A_contract_the_host_does_not_dispatch_is_answered_as_the_wire_answers_it()
    {
        // Arrange
        await using var host = await StartHostAsync();
        const string unknown = "Conduit.Tests.Support.NoSuchCommand";
        var payload = JsonSerializer.SerializeToElement(new { });

        // Act
        var preflight = await PreflightAsync(host, unknown, payload);
        var wire = await WireAsync(host, unknown, payload);

        // Assert
        preflight.Admitted.Should().BeFalse();
        preflight.Refusal!.Code.Should().Be("InternalError").And.Be(wire.Error!.Code);
    }

    [TestMethod]
    public async Task A_routed_contract_runs_none_of_this_hosts_checks()
    {
        // Arrange -- the wire forwards a routed request unread, so its checks are the receiving host's
        await using var host = await StartHostAsync(conduit =>
            conduit.AddEndpoint("elsewhere", endpoint => endpoint.RouteType<PreflightCommand>()));

        // Act
        var preflight = await PreflightAsync(host, typeof(PreflightCommand).FullName!, Payload(new PreflightCommand("refuse")));

        // Assert
        preflight.Should().Be(new WirePreflightResult(Admitted: true, Refusal: null));
        Probe(host).Sites.Should().BeEmpty();
    }

    [TestMethod]
    public async Task It_records_nothing_and_leaves_its_callers_context_in_place()
    {
        // Arrange -- a caller dispatching with an observer attached; a gate that sees the context an inbox gate records
        await using var host = await StartHostAsync(
            configureServices: services => services.AddSingleton<IInboundInterceptor, PreflightGate>());
        var collector = new ObservationCollector();
        var caller = MessageContext.StartDispatch();
        caller.AttachObserver(collector);
        try
        {
            // Act
            var preflight = await PreflightAsync(host, typeof(PreflightCommand).FullName!, Payload(new PreflightCommand("admit")));

            // Assert -- the gates ran under no context, nothing was observed, and the caller's context is current again
            preflight.Admitted.Should().BeTrue();
            Probe(host).GateContexts.Should().ContainSingle().Which.Should().BeNull();
            collector.Collected.Should().BeEmpty();
            MessageContext.Current.Should().BeSameAs(caller);
        }
        finally
        {
            MessageContext.RestoreContext(null);
        }
    }

    private static Task<TestHost> StartHostAsync(
        Action<ConduitBuilder>? configureConduit = null,
        Action<IServiceCollection>? configureServices = null)
        => TestHost.StartAsync(builder =>
        {
            builder.Services.AddSingleton<PreflightProbe>();
            builder.Services.AddConduit(conduit =>
            {
                conduit.AddPipeline(typeof(PreflightCheckBehavior<,>));
                conduit.AddPipeline(typeof(PreflightOtherBehavior<,>));
                conduit.AddPipeline(typeof(SecondPreflightCheckBehavior<,>));
                configureConduit?.Invoke(conduit);
            });
            configureServices?.Invoke(builder.Services);
        });

    private static Task<WirePreflightResult> PreflightAsync(TestHost host, string contractName, JsonElement payload)
        => ConduitRuntime.PreflightWireRequestAsync(contractName, payload, host.Services, CancellationToken.None);

    private static Task<DispatchResult<object>> WireAsync(TestHost host, string contractName, JsonElement payload)
        => host.GetRequiredService<IWireMediator>()
            .HandleWireRequestAsync(new DispatchEnvelope(contractName, payload), CancellationToken.None);

    private static PreflightProbe Probe(TestHost host) => host.GetRequiredService<PreflightProbe>();

    private static JsonElement Payload<T>(T message) => JsonSerializer.SerializeToElement(message, ConduitRuntime.JsonOptions);

    /// <summary>A refusal the preflight returned equals the wire's, field by field (the data compared as JSON).</summary>
    private static void ShouldBeTheSameAnswer(DispatchError? preflight, DispatchError? wire)
    {
        preflight.Should().NotBeNull();
        wire.Should().NotBeNull();
        preflight.Code.Should().Be(wire.Code);
        preflight.Message.Should().Be(wire.Message);
        preflight.Status.Should().Be(wire.Status);
        preflight.Type.Should().Be(wire.Type);
        preflight.Reference.Should().Be(wire.Reference);
        preflight.Details.Should().BeEquivalentTo(wire.Details);
        preflight.Data?.GetRawText().Should().Be(wire.Data?.GetRawText());
        (preflight.Data is null).Should().Be(wire.Data is null);
    }
}
