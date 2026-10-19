namespace Conduit.Jobs.Tests.Support;

/// <summary>One time a dispatch retry test's handler ran: which scope it was resolved from, and which attempt it was.</summary>
/// <param name="ScopeId">The <see cref="ScopeProbe.Id"/> of the scope the handler was resolved from.</param>
/// <param name="AttemptNumber">The running <see cref="Conduit.Runtime.DispatchAttempt"/>'s number, or null outside one.</param>
public sealed record RetrySighting(Guid ScopeId, int? AttemptNumber);
