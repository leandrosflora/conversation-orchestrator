using System.Text.Json;
using Confluent.Kafka;
using Microsoft.Extensions.Options;
using conversation_orchestrator.Application.Ports.Outbound;
using conversation_orchestrator.Configuration;

namespace conversation_orchestrator.Adapters.Outbound.Messaging;

public class KafkaConversationEventPublisher(
    IProducer<string, string> producer,
    IOptions<KafkaOptions> options,
    ILogger<KafkaConversationEventPublisher> logger) : IConversationEventPublisher
{
    public Task PublishIntentDetectedAsync(IntentDetectedEvent evt, CancellationToken cancellationToken) =>
        PublishAsync(options.Value.IntentDetectedTopic, evt.ConversationId, evt, cancellationToken);

    public Task PublishConversationStateChangedAsync(ConversationStateChangedEvent evt, CancellationToken cancellationToken) =>
        PublishAsync(options.Value.ConversationStateChangedTopic, evt.ConversationId, evt, cancellationToken);

    private async Task PublishAsync<T>(string topic, string partitionKey, T value, CancellationToken cancellationToken)
    {
        try
        {
            var json = JsonSerializer.Serialize(value);
            await producer.ProduceAsync(
                topic, new Message<string, string> { Key = partitionKey, Value = json }, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogError(
                ex,
                "Failed to publish event for conversation {ConversationId} to Kafka topic {Topic}",
                partitionKey,
                topic);
        }
    }
}
