using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Confluent.Kafka;
using Microsoft.Extensions.Options;
using conversation_orchestrator.Application.Ports.Outbound;
using conversation_orchestrator.Configuration;
using conversation_orchestrator.Platform;

namespace conversation_orchestrator.Adapters.Outbound.Messaging;

public class KafkaConversationEventPublisher(
    IProducer<string, string> producer,
    IOptions<KafkaOptions> options,
    TenantContext tenantContext,
    PlatformMetrics metrics,
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
            var headers = new Headers
            {
                { "tenant-id", Encoding.UTF8.GetBytes(tenantContext.TenantId) }
            };
            var activity = Activity.Current;
            if (activity is not null)
            {
                headers.Add("traceparent", Encoding.UTF8.GetBytes(activity.Id ?? string.Empty));
                if (!string.IsNullOrWhiteSpace(activity.TraceStateString))
                    headers.Add("tracestate", Encoding.UTF8.GetBytes(activity.TraceStateString));
            }

            await producer.ProduceAsync(
                topic,
                new Message<string, string>
                {
                    Key = partitionKey,
                    Value = JsonSerializer.Serialize(value),
                    Headers = headers
                },
                cancellationToken);
            metrics.Increment("orchestrator_kafka_events_total", ("topic", topic), ("outcome", "success"));
        }
        catch (Exception ex)
        {
            metrics.Increment("orchestrator_kafka_events_total", ("topic", topic), ("outcome", "error"));
            logger.LogError(ex, "Failed to publish event for conversation {ConversationId} to {Topic}", partitionKey, topic);
        }
    }
}
