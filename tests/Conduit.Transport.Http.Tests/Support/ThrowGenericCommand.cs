using Conduit.Attributes;
using Conduit.Messages;

namespace Conduit.Transport.Http.Tests.Support;

[Dispatchable]
public record ThrowGenericCommand() : ICommand;
