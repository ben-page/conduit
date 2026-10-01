using AwesomeAssertions;
using Conduit.Configuration;
using Conduit.Exceptions;
using Conduit.Generated;
using Conduit.Runtime;
using Conduit.TestUtils;
using Conduit.Tests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace Conduit.Tests.Integration;

/// <summary>
/// The generated dispatch calls a message's <c>Validate()</c> INNERMOST -- after every pipeline behavior, immediately
/// before the handler (or, for a fire-and-forget command, immediately before the enqueue). A behavior that refuses
/// (authorization) therefore never runs the caller's input, and a behavior that passes still sees the input validated.
/// One pair of tests per generated dispatch path.
/// </summary>
[TestClass]
public class ValidateOrderShould
{
    // --- Query (Fetch) ---

    [TestMethod]
    public async Task Query_ABehaviorThatRefuses_RunsBeforeValidate()
    {
        await using var host = StartHost(typeof(RefusingBehavior<,>));
        var record = host.GetRequiredService<DispatchOrderRecord>();
        var mediator = host.GetRequiredService<IMediator>();

        var act = () => mediator.FetchAsync(new InvalidOrderQuery(record));

        await act.Should().ThrowAsync<RefusedByBehaviorException>();
        record.Entries.Should().Equal("behavior");
    }

    [TestMethod]
    public async Task Query_APassingBehavior_StillValidates()
    {
        await using var host = StartHost(typeof(PassingBehavior<,>));
        var record = host.GetRequiredService<DispatchOrderRecord>();
        var mediator = host.GetRequiredService<IMediator>();

        var act = () => mediator.FetchAsync(new InvalidOrderQuery(record));

        await act.Should().ThrowAsync<BadRequestException>();
        record.Entries.Should().Equal("behavior", "validate");
    }

    // --- Command with a result (Execute) ---

    [TestMethod]
    public async Task CommandWithResult_ABehaviorThatRefuses_RunsBeforeValidate()
    {
        await using var host = StartHost(typeof(RefusingBehavior<,>));
        var record = host.GetRequiredService<DispatchOrderRecord>();
        var mediator = host.GetRequiredService<IMediator>();

        var act = () => mediator.ExecuteAsync(new InvalidOrderResultCommand(record));

        await act.Should().ThrowAsync<RefusedByBehaviorException>();
        record.Entries.Should().Equal("behavior");
    }

    [TestMethod]
    public async Task CommandWithResult_APassingBehavior_StillValidates()
    {
        await using var host = StartHost(typeof(PassingBehavior<,>));
        var record = host.GetRequiredService<DispatchOrderRecord>();
        var mediator = host.GetRequiredService<IMediator>();

        var act = () => mediator.ExecuteAsync(new InvalidOrderResultCommand(record));

        await act.Should().ThrowAsync<BadRequestException>();
        record.Entries.Should().Equal("behavior", "validate");
    }

    // --- Void command (Execute) ---

    [TestMethod]
    public async Task VoidCommand_ABehaviorThatRefuses_RunsBeforeValidate()
    {
        await using var host = StartHost(typeof(RefusingBehavior<,>));
        var record = host.GetRequiredService<DispatchOrderRecord>();
        var mediator = host.GetRequiredService<IMediator>();

        var act = () => mediator.ExecuteAsync(new InvalidOrderVoidCommand(record));

        await act.Should().ThrowAsync<RefusedByBehaviorException>();
        record.Entries.Should().Equal("behavior");
    }

    [TestMethod]
    public async Task VoidCommand_APassingBehavior_StillValidates()
    {
        await using var host = StartHost(typeof(PassingBehavior<,>));
        var record = host.GetRequiredService<DispatchOrderRecord>();
        var mediator = host.GetRequiredService<IMediator>();

        var act = () => mediator.ExecuteAsync(new InvalidOrderVoidCommand(record));

        await act.Should().ThrowAsync<BadRequestException>();
        record.Entries.Should().Equal("behavior", "validate");
    }

    // --- Void command (ExecuteVoid -- the object-returning wrapper over Execute that the generated mediator's wire
    //     request dispatch, DispatchRequestAsync, calls for a non-fire-and-forget void command) ---

    [TestMethod]
    public async Task VoidCommandExecuteVoid_ABehaviorThatRefuses_RunsBeforeValidate()
    {
        await using var host = StartHost(typeof(RefusingBehavior<,>));
        var record = host.GetRequiredService<DispatchOrderRecord>();
        var options = host.GetRequiredService<ConduitOptions>();

        var act = () => ConduitDispatch.ExecuteVoid(
            new InvalidOrderVoidCommand(record), host.Services, options, CancellationToken.None);

        await act.Should().ThrowAsync<RefusedByBehaviorException>();
        record.Entries.Should().Equal("behavior");
    }

    [TestMethod]
    public async Task VoidCommandExecuteVoid_APassingBehavior_StillValidates()
    {
        await using var host = StartHost(typeof(PassingBehavior<,>));
        var record = host.GetRequiredService<DispatchOrderRecord>();
        var options = host.GetRequiredService<ConduitOptions>();

        var act = () => ConduitDispatch.ExecuteVoid(
            new InvalidOrderVoidCommand(record), host.Services, options, CancellationToken.None);

        await act.Should().ThrowAsync<BadRequestException>();
        record.Entries.Should().Equal("behavior", "validate");
    }

    // --- Fire-and-forget void command (Execute -- behaviors in the caller's context, handler enqueued) ---

    [TestMethod]
    public async Task VoidFireAndForgetCommand_ABehaviorThatRefuses_RunsBeforeValidate()
    {
        await using var host = StartHost(typeof(RefusingBehavior<,>));
        var record = host.GetRequiredService<DispatchOrderRecord>();
        var mediator = host.GetRequiredService<IMediator>();

        var act = () => mediator.ExecuteAsync(new InvalidOrderFireAndForgetCommand(record));

        await act.Should().ThrowAsync<RefusedByBehaviorException>();
        await DrainBackgroundDispatcherAsync(host);
        record.Entries.Should().Equal("behavior");
    }

    [TestMethod]
    public async Task VoidFireAndForgetCommand_APassingBehavior_StillValidates()
    {
        await using var host = StartHost(typeof(PassingBehavior<,>));
        var record = host.GetRequiredService<DispatchOrderRecord>();
        var mediator = host.GetRequiredService<IMediator>();

        // The refusal reaches the caller of Execute -- validation runs on the caller's side of the enqueue.
        var act = () => mediator.ExecuteAsync(new InvalidOrderFireAndForgetCommand(record));

        await act.Should().ThrowAsync<BadRequestException>();

        // Nothing was enqueued: drain the (sequential, FIFO) background dispatcher, then prove the handler never ran.
        await DrainBackgroundDispatcherAsync(host);
        record.Entries.Should().Equal("behavior", "validate");
    }

    // --- Fire-and-forget void command (ExecuteVoidWire -- what DispatchRequestAsync calls for a fire-and-forget void
    //     command: the handler runs inline, not on the background dispatcher) ---

    [TestMethod]
    public async Task VoidFireAndForgetCommandExecuteVoidWire_ABehaviorThatRefuses_RunsBeforeValidate()
    {
        await using var host = StartHost(typeof(RefusingBehavior<,>));
        var record = host.GetRequiredService<DispatchOrderRecord>();
        var options = host.GetRequiredService<ConduitOptions>();

        var act = () => ConduitDispatch.ExecuteVoidWire(
            new InvalidOrderFireAndForgetCommand(record), host.Services, options, CancellationToken.None);

        await act.Should().ThrowAsync<RefusedByBehaviorException>();
        record.Entries.Should().Equal("behavior");
    }

    [TestMethod]
    public async Task VoidFireAndForgetCommandExecuteVoidWire_APassingBehavior_StillValidates()
    {
        await using var host = StartHost(typeof(PassingBehavior<,>));
        var record = host.GetRequiredService<DispatchOrderRecord>();
        var options = host.GetRequiredService<ConduitOptions>();

        var act = () => ConduitDispatch.ExecuteVoidWire(
            new InvalidOrderFireAndForgetCommand(record), host.Services, options, CancellationToken.None);

        await act.Should().ThrowAsync<BadRequestException>();
        record.Entries.Should().Equal("behavior", "validate");
    }

    private static TestWebHost StartHost(Type behavior) =>
        TestWebHost.Start(builder =>
        {
            builder.Services.AddSingleton<DispatchOrderRecord>();
            builder.Services.AddConduit(conduit => conduit.AddPipeline(behavior));
        });

    /// <summary>
    /// The background dispatcher runs work items one at a time, in order. A sentinel enqueued now completes only after
    /// everything enqueued before it -- so once it completes, any handler the dispatch had enqueued has already run.
    /// </summary>
    private static async Task DrainBackgroundDispatcherAsync(TestWebHost host)
    {
        var dispatcher = host.GetRequiredService<ConduitBackgroundDispatcher>();
        var drained = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        dispatcher.Enqueue(_ =>
        {
            drained.SetResult();
            return Task.CompletedTask;
        });
        await drained.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }
}
