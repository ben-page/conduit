# AI-Assisted Development

Conduit was built with significant AI assistance (Claude). The code, tests, and documentation were largely AI-generated.

This does not mean it was "vibe coded." Every feature went through a deliberate design process: requirements were discussed, multiple approaches were evaluated with trade-offs, and a design was approved before implementation began. Architecture, API design, and product direction were human-driven. The AI implemented those decisions.

The codebase enforces quality through strict static analysis (`TreatWarningsAsErrors`, `EnforceCodeStyleInBuild`), comprehensive test coverage (MSTest + AwesomeAssertions, no mocks), and test-driven development. Code that doesn't pass the analyzers doesn't compile.

If you're contributing: AI-generated code is held to the same standard as hand-written code. If it doesn't meet the project's conventions and standards, it will not be accepted.
