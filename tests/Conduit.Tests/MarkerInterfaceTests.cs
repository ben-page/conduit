using AwesomeAssertions;

namespace Conduit.Tests;

[TestClass]
public class MarkerInterfaceTests
{
    private record TestVoidCommand : ICommand;
    private record TestResultCommand(string Value) : ICommand<string>;
    private record TestQuery(int Id) : IQuery<string>;
    private record TestEvent(string Data) : IEvent;

    [TestMethod]
    public void VoidCommand_ImplementsICommand()
    {
        ICommand command = new TestVoidCommand();
        command.Should().NotBeNull();
    }

    [TestMethod]
    public void ResultCommand_ImplementsICommand()
    {
        ICommand<string> command = new TestResultCommand("test");
        command.Should().BeAssignableTo<ICommand>();
    }

    [TestMethod]
    public void Command_Validate_DefaultIsNoOp()
    {
        ICommand command = new TestVoidCommand();
        // Should not throw - calls default interface method
        command.Validate();
    }

    [TestMethod]
    public void Query_Validate_DefaultIsNoOp()
    {
        IQuery<string> query = new TestQuery(1);
        query.Validate();
    }
}
