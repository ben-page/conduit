using Conduit.Messages;

namespace Conduit.Transport.RabbitMQ.Tests.Support;

public record CaptureContextCommand : ICommand<MessageContextSnapshot>;
