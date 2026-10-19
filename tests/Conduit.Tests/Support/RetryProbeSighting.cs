namespace Conduit.Tests.Support;

/// <summary>One time a dispatch retry test's handler or behavior ran, and what it saw.</summary>
/// <param name="Site">Who ran: a handler's message type name, or a behavior's.</param>
/// <param name="ScopeId">The <see cref="AttemptProbe.Id"/> of the scope it was resolved from.</param>
/// <param name="AttemptNumber">The running <see cref="Conduit.Runtime.DispatchAttempt"/>'s number, or null outside one.</param>
/// <param name="MessageId">The dispatch's <see cref="Conduit.Runtime.MessageContext.MessageId"/>.</param>
public sealed record RetryProbeSighting(string Site, Guid ScopeId, int? AttemptNumber, string? MessageId);
