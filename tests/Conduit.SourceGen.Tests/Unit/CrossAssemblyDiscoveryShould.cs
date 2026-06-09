using AwesomeAssertions;
using Conduit.SourceGen.Tests.Support;

namespace Conduit.SourceGen.Tests.Unit;

[TestClass]
public sealed class CrossAssemblyDiscoveryShould : GeneratorTestBase
{
    private const string ContractAndHandlerSource = @"
using System.Threading;
using System.Threading.Tasks;
using Conduit.Messages;
using Conduit.Handlers;
namespace ModuleA
{
    public sealed record DoThingCommand(int Value) : ICommand<int>;
    public sealed class DoThingHandler : ICommandHandler<DoThingCommand, int>
    {
        public Task<int> HandleAsync(DoThingCommand command, CancellationToken ct) => Task.FromResult(command.Value);
    }
}";

    [TestMethod]
    public void DiscoverHandlerDeclaredInReferencedAssembly()
    {
        var moduleRef = CompileReference("ModuleA", ContractAndHandlerSource);

        // The compilation under test has NO handler syntax of its own — only the reference.
        const string hostSource = "namespace Host { internal sealed class Marker { } }";
        var (trees, diagnostics) = RunGenerator(hostSource, moduleRef);

        var registration = FindGeneratedSource(trees, "ConduitRegistration");
        var mediator = FindGeneratedSource(trees, "ConduitMediator");

        registration.Should().NotBeNull();
        registration!.Should().Contain("ModuleA.DoThingHandler");          // DI registration emitted
        mediator.Should().NotBeNull();
        mediator!.Should().Contain("ModuleA.DoThingCommand");              // dispatch branch emitted
        diagnostics.Should().NotContain(d => d.Id == "CONDUIT001");        // NOT reported as "no handler"
    }

    [TestMethod]
    public void ReportDiagnosticForInaccessibleReferencedHandler()
    {
        const string src = @"
using System.Threading;
using System.Threading.Tasks;
using Conduit.Messages;
using Conduit.Handlers;
namespace ModuleB
{
    public sealed record HiddenCommand(int V) : ICommand<int>;
    internal sealed class HiddenHandler : ICommandHandler<HiddenCommand, int>   // INTERNAL -> not referenceable by host
    {
        public Task<int> HandleAsync(HiddenCommand c, CancellationToken ct) => Task.FromResult(c.V);
    }
}";
        var moduleRef = CompileReference("ModuleB", src);
        var (_, diagnostics) = RunGenerator("namespace Host { internal sealed class M {} }", moduleRef);

        diagnostics.Should().Contain(d => d.Id == "CONDUIT004"); // HandlerNotAccessible
    }
}
