using Conduit.Messages;

namespace Conduit.Transport.AzureServiceBus.Tests.Support;

public record CaptureContextCommand : ICommand<MessageContextSnapshot>;
