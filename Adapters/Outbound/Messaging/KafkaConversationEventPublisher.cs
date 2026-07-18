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
    public const string ActivitySourceName = "conversation-orchestrator.kafka";
    private static readonly ActivitySource ActivitySource = new(ActivitySourceName);

    public Task PublishIntentDetectedAsync(IntentDetectedEvent evt, CancellationToken cancellationToken) =>
        PublishAsync(options.Value.IntentDetectedTopic, evt.ConversationId, evt, cancellationToken);

    public Task PublishConversationStateChangedAsync(
        ConversationStateChangedEvent evt,
        CancellationToken cancellationToken) =>
        PublishAsync(options.Value.ConversationStateChangedTopic, evt.ConversationId, evt, cancellationToken);

    private async Task PublishAsync<T>(
        string topic,
        string partitionKey,
        T value,
        CancellationToken cancellationToken)
    {
        using var producerActivity = ActivitySource.StartActivity(
            $"publish {topic}",
            ActivityKind.Producer);
        producerActivity?.SetTag("messaging.system", "kafka");
        producerActivity?.SetTag("messaging.destination.name", topic);
        producerActivity?.SetTag("messaging.operation.type", "publish");

        var traceActivity = producerActivity ?? Activity.Current;
        var headers = new Headers
        {
            { "tenant-id", Encoding.UTF8.GetBytes(tenantContext.TenantId) }
        };

        if (traceActivity?.Id is { Length: > 0 } traceParent)
        {
            headers.Add("traceparent", Encoding.UTF8.GetBytes(traceParent));
        }

        if (!string.IsNullOrWhiteSpace(traceActivity?.TraceStateString))
        {
            headers.Add("tracestate", Encoding.UTF8.GetBytes(traceActivity.TraceStateString));
        }

        try
        {
            await producer.ProduceAsync(
                topic,
                new Message<string, string>
                {
                    Key = partitionKey,
                    Value = JsonSerializer.Serialize(value),
                    Headers = headers
                },
                cancellationToken);

            metrics.Increment(
                "orchestrator_kafka_events_total",
                ("topic", topic),
                ("outcome", "success"));
        }
        catch (Exception ex)
        {
            producerActivity?.SetStatus(ActivityStatusCode.Error, ex.GetType().Name);
            metrics.Increment(
                "orchestrator_kafka_events_total",
                ("topic", topic),
                ("outcome", "error"));
            logger.LogError(
                ex,
                "Failed to publish event for conversation {ConversationId} to Kafka topic {Topic}",
                partitionKey,
                topic);
        }
    }
}
