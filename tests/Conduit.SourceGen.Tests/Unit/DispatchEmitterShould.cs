using AwesomeAssertions;
using Conduit.SourceGen.Tests.Support;

namespace Conduit.SourceGen.Tests.Unit;

[TestClass]
public class DispatchEmitterShould : GeneratorTestBase
{
    [TestMethod]
    public void EmitExecuteForCommandWithResult()
    {
        var source = @"
using Conduit.Handlers;
using Conduit.Messages;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace App.Commands
{
    public record CreateOrder(string CustomerId) : ICommand<Guid>;

    public class CreateOrderHandler : ICommandHandler<CreateOrder, Guid>
    {
        public Task<Guid> HandleAsync(CreateOrder command, CancellationToken cancellationToken)
            => Task.FromResult(Guid.NewGuid());
    }
}
";
        var (trees, _) = RunGenerator(source);

        var dispatchSource = FindGeneratedSource(trees, "ConduitDispatch");
        dispatchSource.Should().NotBeNull();
        dispatchSource.Should().Contain("Execute(");
        dispatchSource.Should().Contain("App.Commands.CreateOrder command");
        dispatchSource.Should().Contain("((Conduit.Messages.ICommand<System.Guid>)command).Validate()");
        dispatchSource.Should().Contain("ICommandHandler<App.Commands.CreateOrder, System.Guid>");
        ShouldValidateInnermost(
            dispatchSource!,
            "Task<System.Guid> Execute(",
            "((Conduit.Messages.ICommand<System.Guid>)command).Validate();",
            "handler.HandleAsync(command");
    }

    [TestMethod]
    public void EmitExecuteForVoidCommand()
    {
        var source = @"
using Conduit.Handlers;
using Conduit.Messages;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace App.Commands
{
    public record DeleteOrder(Guid Id) : ICommand;

    public class DeleteOrderHandler : ICommandHandler<DeleteOrder>
    {
        public Task HandleAsync(DeleteOrder command, CancellationToken cancellationToken)
            => Task.CompletedTask;
    }
}
";
        var (trees, _) = RunGenerator(source);

        var dispatchSource = FindGeneratedSource(trees, "ConduitDispatch");
        dispatchSource.Should().NotBeNull();
        dispatchSource.Should().Contain("async Task Execute(");
        dispatchSource.Should().Contain("ExecuteVoid(");
        ShouldValidateInnermost(
            dispatchSource!,
            "async Task Execute(",
            "((Conduit.Messages.ICommand)command).Validate();",
            "handler.HandleAsync(command");
    }

    [TestMethod]
    public void EmitFetchForQuery()
    {
        var source = @"
using Conduit.Handlers;
using Conduit.Messages;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace App.Queries
{
    public record GetOrder(Guid Id) : IQuery<string>;

    public class GetOrderHandler : IQueryHandler<GetOrder, string>
    {
        public Task<string> HandleAsync(GetOrder query, CancellationToken cancellationToken)
            => Task.FromResult(""order"");
    }
}
";
        var (trees, _) = RunGenerator(source);

        var dispatchSource = FindGeneratedSource(trees, "ConduitDispatch");
        dispatchSource.Should().NotBeNull();
        dispatchSource.Should().Contain("Fetch(");
        dispatchSource.Should().Contain("App.Queries.GetOrder query");
        ShouldValidateInnermost(
            dispatchSource!,
            "Task<string> Fetch(",
            "((Conduit.Messages.IQuery<string>)query).Validate();",
            "handler.HandleAsync(query");
    }

    [TestMethod]
    public void EmitValidateInnermostForFireAndForgetVoidCommand()
    {
        var source = @"
using Conduit.Attributes;
using Conduit.Handlers;
using Conduit.Messages;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace App.Commands
{
    [FireAndForget]
    public record ImportOrders(string Path) : ICommand;

    public class ImportOrdersHandler : ICommandHandler<ImportOrders>
    {
        public Task HandleAsync(ImportOrders command, CancellationToken cancellationToken)
            => Task.CompletedTask;
    }
}
";
        var (trees, _) = RunGenerator(source);

        var dispatchSource = FindGeneratedSource(trees, "ConduitDispatch");
        dispatchSource.Should().NotBeNull();

        // Execute: behaviors run in the caller's context; Validate() runs inside handlerCall, before the enqueue.
        var execute = ShouldValidateInnermost(
            dispatchSource!,
            "public static Task Execute(",
            "((Conduit.Messages.ICommand)command).Validate();",
            "handler.HandleAsync(command");
        execute.IndexOf("((Conduit.Messages.ICommand)command).Validate();", StringComparison.Ordinal)
            .Should().BeLessThan(execute.IndexOf("dispatcher.Enqueue(", StringComparison.Ordinal));

        // ExecuteVoidWire: the wire-inbound entry runs the handler inline; Validate() is still innermost.
        ShouldValidateInnermost(
            dispatchSource!,
            "ExecuteVoidWire(",
            "((Conduit.Messages.ICommand)command).Validate();",
            "handler.HandleAsync(command");
    }

    /// <summary>
    /// Asserts, within one generated dispatch method, that the message's <c>Validate()</c> call is emitted exactly
    /// once, AFTER the pipeline is resolved and assembled (<c>ResolvePipelines</c>, and inside the <c>handlerCall</c>
    /// that <c>ExecuteWithBehaviors</c> wraps) and BEFORE the handler's <c>HandleAsync</c> -- innermost, so every
    /// behavior (authorization included) runs before the caller's input is validated. Returns the method's text.
    /// </summary>
    private static string ShouldValidateInnermost(
        string dispatchSource,
        string methodHeader,
        string validateCall,
        string handlerInvocation)
    {
        var start = dispatchSource.IndexOf(methodHeader, StringComparison.Ordinal);
        start.Should().BeGreaterThanOrEqualTo(0, $"the generated source should declare {methodHeader}");
        var end = dispatchSource.IndexOf("    public static ", start + methodHeader.Length, StringComparison.Ordinal);
        var method = end < 0 ? dispatchSource[start..] : dispatchSource[start..end];

        var validate = method.IndexOf(validateCall, StringComparison.Ordinal);
        validate.Should().BeGreaterThanOrEqualTo(0, $"{methodHeader} should call {validateCall}");
        method.LastIndexOf(validateCall, StringComparison.Ordinal).Should().Be(validate, "Validate() is emitted once");

        var resolvePipelines = method.IndexOf("ResolvePipelines<", StringComparison.Ordinal);
        var handlerCall = method.IndexOf("handlerCall = () =>", StringComparison.Ordinal);
        var handle = method.IndexOf(handlerInvocation, StringComparison.Ordinal);
        resolvePipelines.Should().BeGreaterThanOrEqualTo(0);
        handlerCall.Should().BeGreaterThanOrEqualTo(0);
        handle.Should().BeGreaterThanOrEqualTo(0);

        validate.Should().BeGreaterThan(resolvePipelines, "Validate() runs after the pipeline is resolved");
        validate.Should().BeGreaterThan(handlerCall, "Validate() runs inside handlerCall, inside every behavior");
        validate.Should().BeLessThan(handle, "Validate() runs immediately before the handler");
        return method;
    }
}
