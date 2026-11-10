using System.Text.Json;
using Amqp;
using Amqp.Framing;

namespace Conduit.Transport.ActiveMQ.Tests.Support;

/// <summary>
/// Reads what the broker holds, through the Artemis management address over its own AMQP connection,
/// so a test asserts on the broker's state rather than on the transport's view of it.
/// </summary>
internal sealed class ArtemisManagement : IAsyncDisposable
{
    private readonly Connection connection;
    private readonly SenderLink sender;
    private readonly ReceiverLink receiver;
    private readonly string replyAddress;

    private ArtemisManagement(Connection connection, SenderLink sender, ReceiverLink receiver, string replyAddress)
    {
        this.connection = connection;
        this.sender = sender;
        this.receiver = receiver;
        this.replyAddress = replyAddress;
    }

    public static async Task<ArtemisManagement> ConnectAsync(string connectionString)
    {
        var connection = await new ConnectionFactory().CreateAsync(new Address(connectionString));
        var session = new Session(connection);

        var attached = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var receiver = new ReceiverLink(
            session,
            $"test-mgmt-recv-{Guid.NewGuid():N}",
            new Source { Dynamic = true },
            (link, attach) => attached.TrySetResult(((Source)attach.Source).Address));
        var replyAddress = await attached.Task.WaitAsync(TimeSpan.FromSeconds(30));
        var sender = new SenderLink(session, $"test-mgmt-send-{Guid.NewGuid():N}", "activemq.management");

        return new ArtemisManagement(connection, sender, receiver, replyAddress);
    }

    /// <summary>The number of messages ever routed to the queue.</summary>
    public async Task<long> MessagesAddedAsync(string queueName) =>
        (await this.ReadQueueAttributeAsync(queueName, "messagesAdded")).GetInt64();

    /// <summary>The queue's filter, or null when it has none.</summary>
    public async Task<string?> FilterAsync(string queueName)
    {
        var filter = await this.ReadQueueAttributeAsync(queueName, "filter");
        return filter.ValueKind == JsonValueKind.String ? filter.GetString() : null;
    }

    /// <summary>The messages in the queue that match <paramref name="filter"/>.</summary>
    public async Task<long> CountMessagesAsync(string queueName, string filter)
    {
        var result = await this.RequestAsync($"queue.{queueName}", "countMessages", attribute: null, [filter]);
        return result.GetInt64();
    }

    /// <summary>
    /// The configuration the queue was created with (<c>durable</c>, <c>auto-delete</c>,
    /// <c>auto-delete-delay</c>, <c>auto-delete-message-count</c>, ...). Artemis reports the auto-delete
    /// delay nowhere else: no queue attribute and no queue listing carries it, only the configuration
    /// <c>updateQueue</c> answers with. The update restates the queue's own filter, the one field a partial
    /// update would otherwise clear, so it changes nothing.
    /// </summary>
    public async Task<JsonElement> QueueConfigurationAsync(string queueName)
    {
        var update = new Dictionary<string, string?>
        {
            ["name"] = queueName,
            ["filter-string"] = await this.FilterAsync(queueName),
        };

        var result = await this.RequestAsync("broker", "updateQueue", attribute: null, [JsonSerializer.Serialize(update)]);
        using var configuration = JsonDocument.Parse(result.GetString()!);
        return configuration.RootElement.Clone();
    }

    public async ValueTask DisposeAsync() => await this.connection.CloseAsync();

    private Task<JsonElement> ReadQueueAttributeAsync(string queueName, string attribute) =>
        this.RequestAsync($"queue.{queueName}", operation: null, attribute, []);

    private async Task<JsonElement> RequestAsync(string resource, string? operation, string? attribute, object[] parameters)
    {
        using var request = new Message(JsonSerializer.Serialize(parameters))
        {
            Properties = new Properties
            {
                ReplyTo = this.replyAddress,
                MessageId = $"test-mgmt-{Guid.NewGuid():N}",
            },
            ApplicationProperties = new ApplicationProperties
            {
                ["_AMQ_ResourceName"] = resource,
            },
        };

        if (operation is not null)
        {
            request.ApplicationProperties["_AMQ_OperationName"] = operation;
        }

        if (attribute is not null)
        {
            request.ApplicationProperties["_AMQ_Attribute"] = attribute;
        }

        await this.sender.SendAsync(request);

        var reply = await this.receiver.ReceiveAsync(TimeSpan.FromSeconds(10))
            ?? throw new TimeoutException($"No management reply for '{resource}' within 10 seconds.");
        this.receiver.Accept(reply);

        var body = reply.Body as string;
        if (reply.ApplicationProperties?["_AMQ_OperationSucceeded"] is false)
        {
            throw new InvalidOperationException($"Management request on '{resource}' failed: {body}");
        }

        // The answer is a JSON array holding the one result; an absent body is a null result
        if (string.IsNullOrEmpty(body))
        {
            return default;
        }

        using var document = JsonDocument.Parse(body);
        return document.RootElement[0].Clone();
    }
}
