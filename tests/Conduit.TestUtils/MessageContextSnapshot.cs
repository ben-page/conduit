namespace Conduit.TestUtils;

public record MessageContextSnapshot(
    string? MessageId,
    string? CausationId,
    string? ConversationId);
