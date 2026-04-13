using Conduit.Messages;

namespace Conduit.Audit.Tests.Support;

public sealed record AuditTestCommand(string Value) : ICommand<string>;
