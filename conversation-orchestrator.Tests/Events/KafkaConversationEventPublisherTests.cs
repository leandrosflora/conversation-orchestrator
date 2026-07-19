using Confluent.Kafka;
using Microsoft.Extensions.Options;
using Moq;
using conversation_orchestrator.Adapters.Outbound.Messaging;
using conversation_orchestrator.Application.Ports.Outbound;
using conversation_orchestrator.Configuration;
using conversation_orchestrator.Platform;
using Xunit;

namespace conversation_orchestrator.Tests.Events;

public class KafkaConversationEventPublisherTests
{
    private const string TenantId = "00000000-0000-0000-0000-000000000001";

    private static readonly KafkaOptions Options = new()
    {
        BootstrapServers = "localhost:9092",
        IntentDetectedTopic = "intent.detected",
        ConversationStateChangedTopic = "conversation.state_changed"
    };

    [Fact]
    public async Task PublishIntentDetectedAsync_ProducerSucceeds_PublishesToIntentTopicWithConversationKey()
    {
        var producer = new Mock<IProducer<string, string>>();
        producer
            .Setup(p => p.ProduceAsync("intent.detected", It.IsAny<Message<string, string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DeliveryResult<string, string>());

        var tenantContext = new TenantContext();
        using var tenantScope = tenantContext.Push(TenantId);
        var publisher = new KafkaConversationEventPublisher(
            producer.Object,
            Microsoft.Extensions.Options.Options.Create(Options),
            tenantContext,
            new PlatformMetrics());

        var evt = new IntentDetectedEvent
        {
            ConversationId = "5511999990000",
            Intent = "debt_renegotiation",
            Confidence = 0.9,
            DetectedAt = DateTimeOffset.UtcNow
        };

        await publisher.PublishIntentDetectedAsync(evt, CancellationToken.None);

        producer.Verify(
            p => p.ProduceAsync(
                "intent.detected",
                It.Is<Message<string, string>>(m => m.Key == "5511999990000" && m.Value.Contains("debt_renegotiation")),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task PublishConversationStateChangedAsync_BrokerUnavailable_PropagatesForOutboxRetry()
    {
        var producer = new Mock<IProducer<string, string>>();
        producer
            .Setup(p => p.ProduceAsync(It.IsAny<string>(), It.IsAny<Message<string, string>>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new ProduceException<string, string>(
                new Error(ErrorCode.Local_Transport, "broker unavailable"),
                new DeliveryResult<string, string>()));

        var tenantContext = new TenantContext();
        using var tenantScope = tenantContext.Push(TenantId);
        var publisher = new KafkaConversationEventPublisher(
            producer.Object,
            Microsoft.Extensions.Options.Options.Create(Options),
            tenantContext,
            new PlatformMetrics());

        var evt = new ConversationStateChangedEvent
        {
            ConversationId = "5511999990000",
            PreviousStage = "started",
            NewStage = "processed",
            ChangedAt = DateTimeOffset.UtcNow
        };

        await Assert.ThrowsAsync<ProduceException<string, string>>(() =>
            publisher.PublishConversationStateChangedAsync(evt, CancellationToken.None));
    }
}
