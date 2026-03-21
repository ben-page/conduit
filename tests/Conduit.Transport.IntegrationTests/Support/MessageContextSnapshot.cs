namespace Conduit.Transport.IntegrationTests.Support;

public record MessageContextSnapshot(
    string? MessageId,
    string? CausationId,
    string? ConversationId);
