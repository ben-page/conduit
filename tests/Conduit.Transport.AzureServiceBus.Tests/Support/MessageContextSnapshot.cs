namespace Conduit.Transport.AzureServiceBus.Tests.Support;

public record MessageContextSnapshot(string MessageId, string? CausationId, string ConversationId);
