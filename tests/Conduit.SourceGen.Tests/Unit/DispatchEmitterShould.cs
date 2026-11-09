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
        var (output, _) = RunGenerator(source);

        var dispatchSource = FindGeneratedSource(output, "ConduitDispatch");
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
        var (output, _) = RunGenerator(source);

        var dispatchSource = FindGeneratedSource(output, "ConduitDispatch");
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
        var (output, _) = RunGenerator(source);

        var dispatchSource = FindGeneratedSource(output, "ConduitDispatch");
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
        var (output, _) = RunGenerator(source);

        var dispatchSource = FindGeneratedSource(output, "ConduitDispatch");
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

    [TestMethod]
    public void RouteACommandWithResultThroughDispatchAttempts()
    {
        // Arrange
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

        // Act
        var (output, _) = RunGenerator(source);
        var dispatchSource = FindGeneratedSource(output, "ConduitDispatch")!;

        // Assert -- Execute hands the old body, as ExecuteAttempt over the attempt's provider, to the boundary
        var execute = MethodText(dispatchSource, "public static async Task<System.Guid> Execute(");
        execute.Should().Contain(
            "return await Conduit.Runtime.ConduitRuntime.DispatchAttemptsAsync<App.Commands.CreateOrder, System.Guid>(");
        execute.Should().Contain(
            "attemptServices => ExecuteAttempt(command, attemptServices, conduitOptions, cancellationToken),");
        execute.Should().NotContain("ResolvePipelines<");

        var attempt = MethodText(dispatchSource, "private static async Task<System.Guid> ExecuteAttempt(");
        attempt.Should().Contain("ResolvePipelines<App.Commands.CreateOrder, System.Guid>(conduitOptions, serviceProvider)");
        attempt.Should().Contain("ExecuteWithBehaviors(command, behaviors, handlerCall, cancellationToken)");
        attempt.Should().NotContain("DispatchAttemptsAsync");
        ShouldValidateInnermost(
            dispatchSource,
            "Task<System.Guid> ExecuteAttempt(",
            "((Conduit.Messages.ICommand<System.Guid>)command).Validate();",
            "handler.HandleAsync(command");
    }

    [TestMethod]
    public void RouteAVoidCommandThroughDispatchAttempts()
    {
        // Arrange
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

        // Act
        var (output, _) = RunGenerator(source);
        var dispatchSource = FindGeneratedSource(output, "ConduitDispatch")!;

        // Assert -- the void body runs as an attempt whose result is Unit
        var execute = MethodText(dispatchSource, "public static async Task Execute(");
        execute.Should().Contain(
            "await Conduit.Runtime.ConduitRuntime.DispatchAttemptsAsync<App.Commands.DeleteOrder, object>(");
        execute.Should().Contain("await ExecuteAttempt(command, attemptServices, conduitOptions, cancellationToken);");
        execute.Should().Contain("return (object)Conduit.Messages.Unit.Value;");
        execute.Should().NotContain("ResolvePipelines<");

        var attempt = MethodText(dispatchSource, "private static async Task ExecuteAttempt(");
        attempt.Should().Contain("ResolvePipelines<App.Commands.DeleteOrder, object>(conduitOptions, serviceProvider)");
        attempt.Should().NotContain("DispatchAttemptsAsync");
        ShouldValidateInnermost(
            dispatchSource,
            "async Task ExecuteAttempt(",
            "((Conduit.Messages.ICommand)command).Validate();",
            "handler.HandleAsync(command");

        // ExecuteVoid still runs through Execute, so the wire path gets the same boundary
        MethodText(dispatchSource, "public static async Task<object?> ExecuteVoid(")
            .Should().Contain("await Execute(command, serviceProvider, conduitOptions, cancellationToken);");
    }

    [TestMethod]
    public void RouteAQueryThroughDispatchAttempts()
    {
        // Arrange
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

        // Act
        var (output, _) = RunGenerator(source);
        var dispatchSource = FindGeneratedSource(output, "ConduitDispatch")!;

        // Assert
        var fetch = MethodText(dispatchSource, "public static async Task<string> Fetch(");
        fetch.Should().Contain(
            "return await Conduit.Runtime.ConduitRuntime.DispatchAttemptsAsync<App.Queries.GetOrder, string>(");
        fetch.Should().Contain("attemptServices => FetchAttempt(query, attemptServices, conduitOptions, cancellationToken),");
        fetch.Should().NotContain("ResolvePipelines<");

        var attempt = MethodText(dispatchSource, "private static async Task<string> FetchAttempt(");
        attempt.Should().Contain("ResolvePipelines<App.Queries.GetOrder, string>(conduitOptions, serviceProvider)");
        attempt.Should().NotContain("DispatchAttemptsAsync");
        ShouldValidateInnermost(
            dispatchSource,
            "Task<string> FetchAttempt(",
            "((Conduit.Messages.IQuery<string>)query).Validate();",
            "handler.HandleAsync(query");
    }

    [TestMethod]
    public void RouteExecuteVoidWireThroughDispatchAttemptsButNotTheLocalEnqueue()
    {
        // Arrange
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

        // Act
        var (output, _) = RunGenerator(source);
        var dispatchSource = FindGeneratedSource(output, "ConduitDispatch")!;

        // Assert -- the wire-inbound entry runs its inline handler as an attempt
        var wire = MethodText(dispatchSource, "public static async Task<object?> ExecuteVoidWire(");
        wire.Should().Contain(
            "return await Conduit.Runtime.ConduitRuntime.DispatchAttemptsAsync<App.Commands.ImportOrders, object>(");
        wire.Should().Contain(
            "await ExecuteVoidWireAttempt(command, attemptServices, conduitOptions, cancellationToken);");
        wire.Should().Contain("return (object)Conduit.Messages.Unit.Value;");
        wire.Should().NotContain("ResolvePipelines<");

        var attempt = MethodText(dispatchSource, "private static async Task ExecuteVoidWireAttempt(");
        attempt.Should().Contain("ResolvePipelines<App.Commands.ImportOrders, object>(conduitOptions, serviceProvider)");
        attempt.Should().Contain("serviceProvider.GetRequiredService<Conduit.Handlers.ICommandHandler<App.Commands.ImportOrders>>()");
        attempt.Should().NotContain("dispatcher.Enqueue(");

        // Assert -- the local Execute still runs the pipeline around an enqueue, on the caller's provider, with no attempt
        var local = MethodText(dispatchSource, "public static Task Execute(");
        local.Should().Contain("dispatcher.Enqueue(");
        local.Should().Contain("ResolvePipelines<App.Commands.ImportOrders, object>(conduitOptions, serviceProvider)");
        local.Should().NotContain("DispatchAttemptsAsync");
        local.Should().NotContain("Attempt(");

        // Assert -- no ExecuteVoid wrapper: the generated mediator's wire path calls ExecuteVoidWire
        dispatchSource.Should().NotContain("ExecuteVoid(");
    }

    [TestMethod]
    public void WriteANestedTypesAttemptInItsCSharpForm()
    {
        // Arrange
        var source = @"
using Conduit.Attributes;
using Conduit.Handlers;
using Conduit.Messages;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace App
{
    public static class Orders
    {
        public record Place(string CustomerId) : ICommand<Guid>;

        public record Cancel(Guid Id) : ICommand;

        public record Find(Guid Id) : IQuery<string>;

        [FireAndForget]
        public record Import(string Path) : ICommand;
    }

    public class PlaceHandler : ICommandHandler<Orders.Place, Guid>
    {
        public Task<Guid> HandleAsync(Orders.Place command, CancellationToken cancellationToken)
            => Task.FromResult(Guid.NewGuid());
    }

    public class CancelHandler : ICommandHandler<Orders.Cancel>
    {
        public Task HandleAsync(Orders.Cancel command, CancellationToken cancellationToken)
            => Task.CompletedTask;
    }

    public class FindHandler : IQueryHandler<Orders.Find, string>
    {
        public Task<string> HandleAsync(Orders.Find query, CancellationToken cancellationToken)
            => Task.FromResult(""order"");
    }

    public class ImportHandler : ICommandHandler<Orders.Import>
    {
        public Task HandleAsync(Orders.Import command, CancellationToken cancellationToken)
            => Task.CompletedTask;
    }
}
";

        // Act
        var (output, _) = RunGenerator(source);
        var dispatchSource = FindGeneratedSource(output, "ConduitDispatch")!;

        // Assert -- every generic argument and parameter type is the C# ('.') form; ConduitDispatch has no
        // string discriminators, so the metadata ('+') form appears nowhere in it
        dispatchSource.Should().Contain("DispatchAttemptsAsync<App.Orders.Place, System.Guid>(");
        dispatchSource.Should().Contain("DispatchAttemptsAsync<App.Orders.Cancel, object>(");
        dispatchSource.Should().Contain("DispatchAttemptsAsync<App.Orders.Find, string>(");
        dispatchSource.Should().Contain("DispatchAttemptsAsync<App.Orders.Import, object>(");
        dispatchSource.Should().Contain("App.Orders.Place command,");
        dispatchSource.Should().NotContain("Orders+");
    }

    /// <summary>
    /// The text of one generated <c>ConduitDispatch</c> method: from <paramref name="methodHeader"/> to the next
    /// member declaration, public or private.
    /// </summary>
    private static string MethodText(string dispatchSource, string methodHeader)
    {
        var start = dispatchSource.IndexOf(methodHeader, StringComparison.Ordinal);
        start.Should().BeGreaterThanOrEqualTo(0, $"the generated source should declare {methodHeader}");
        var from = start + methodHeader.Length;
        var nextPublic = dispatchSource.IndexOf("    public static ", from, StringComparison.Ordinal);
        var nextPrivate = dispatchSource.IndexOf("    private static ", from, StringComparison.Ordinal);
        var end = new[] { nextPublic, nextPrivate }.Where(index => index >= 0).DefaultIfEmpty(dispatchSource.Length).Min();
        return dispatchSource[start..end];
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
