using Conduit.Attributes;
using Conduit.Messages;

namespace Conduit.TypeGen.Tests.Other;

public record TestExplicitCommand(string Data) : ICommand;

public record TestOrphanCommand(string Data) : ICommand;

[ClientVisible]
public record TestItemDto(Guid Id, string Name);
