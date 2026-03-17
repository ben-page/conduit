namespace Conduit.Transport.RabbitMQ.Tests.Support;

public record MessageContextSnapshot(string MessageId, string? CausationId, string ConversationId);
