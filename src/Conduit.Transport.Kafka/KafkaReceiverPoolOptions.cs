namespace Conduit.Transport.Kafka;

public sealed class KafkaReceiverPoolOptions
{
    public required string Name { get; init; }
    public int MaxDegreeOfParallelism { get; set; } = 1;
    public List<string> MessageTypes { get; } = [];

    public void Handle(string messageTypeFullName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(messageTypeFullName);
        this.MessageTypes.Add(messageTypeFullName);
    }
}
