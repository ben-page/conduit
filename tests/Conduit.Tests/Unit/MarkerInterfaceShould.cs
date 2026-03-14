using AwesomeAssertions;
using Conduit.Attributes;
using Conduit.Messages;

namespace Conduit.Tests.Unit;

[TestClass]
public class MarkerInterfaceShould
{
    [NotDispatchable]
    private record TestVoidCommand : ICommand;

    [NotDispatchable]
    private record TestResultCommand(string Value) : ICommand<string>;

    [NotDispatchable]
    private record TestQuery(int Id) : IQuery<string>;

    [NotDispatchable]
    private record TestEvent(string Data) : IEvent;

    [TestMethod]
    public void AllowVoidCommandToImplementICommand()
    {
        ICommand command = new TestVoidCommand();
        command.Should().NotBeNull();
    }

    [TestMethod]
    public void AllowResultCommandToImplementICommand()
    {
        ICommand<string> command = new TestResultCommand("test");
        command.Should().BeAssignableTo<ICommand>();
    }

    [TestMethod]
    public void DefaultCommandValidateToNoOp()
    {
        ICommand command = new TestVoidCommand();
        // Should not throw - calls default interface method
        command.Validate();
    }

    [TestMethod]
    public void DefaultQueryValidateToNoOp()
    {
        IQuery<string> query = new TestQuery(1);
        query.Validate();
    }
}
