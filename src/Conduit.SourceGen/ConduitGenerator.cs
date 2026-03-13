using Microsoft.CodeAnalysis;

namespace Conduit.SourceGen;

[Generator(LanguageNames.CSharp)]
public sealed class ConduitGenerator : IIncrementalGenerator
{
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        // Will add providers in subsequent tasks
    }
}
