using Conduit.Attributes;
using Conduit.Messages;

namespace Conduit.TypeGen.Tests.Other;

public record TestExplicitCommand(string Data) : ICommand;

public record TestOrphanCommand(string Data) : ICommand;

[ClientVisible]
public record TestItemDto(Guid Id, string Name);

public enum TestAccessLevel { Read, Write, Admin }

[ClientVisible]
public record TestCurrentUserDto(string UserId, IReadOnlyCollection<TestAccessLevel> Permissions);
