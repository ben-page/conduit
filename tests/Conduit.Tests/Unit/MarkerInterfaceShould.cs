using AwesomeAssertions;
using Conduit.Messages;

namespace Conduit.Tests.Unit;

[TestClass]
public class MarkerInterfaceShould
{
    private record TestVoidCommand : ICommand;

    private record TestResultCommand(string Value) : ICommand<string>;

    private record TestQuery(int Id) : IQuery<string>;

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
