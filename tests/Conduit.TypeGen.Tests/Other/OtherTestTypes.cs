using Conduit.Attributes;
using Conduit.Messages;

namespace Conduit.TypeGen.Tests.Other;

[Dispatchable]
public record TestExplicitCommand(string Data) : ICommand;

public record TestOrphanCommand(string Data) : ICommand;
