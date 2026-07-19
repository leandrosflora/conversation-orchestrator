using System.Text.Json;
using conversation_orchestrator.Application.Outbox;
using conversation_orchestrator.Application.Ports.Outbound;
using conversation_orchestrator.Domain;
using conversation_orchestrator.Platform;

namespace conversation_orchestrator.Adapters.Outbound.Persistence;

public sealed class OutboxDispatcherService(
    IOutboxStore outboxStore,
    IServiceScopeFactory scopeFactory,
    PlatformMetrics metrics,
    ILogger<OutboxDispatcherService> logger) : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan ClaimLease = TimeSpan.FromSeconds(90);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            IReadOnlyList<OutboxEnvelope> batch;
            try
            {
                batch = await outboxStore.ClaimBatchAsync(20, ClaimLease, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to claim orchestrator outbox batch");
                metrics.Increment("orchestrator_outbox_claim_failures_total");
                await Task.Delay(PollInterval, stoppingToken);
                continue;
            }

            if (batch.Count == 0)
            {
                await Task.Delay(PollInterval, stoppingToken);
                continue;
            }

            foreach (var envelope in batch)
            {
                await DispatchOneAsync(envelope, stoppingToken);
            }
        }
    }

    private async Task DispatchOneAsync(OutboxEnvelope envelope, CancellationToken cancellationToken)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var tenantContext = scope.ServiceProvider.GetRequiredService<TenantContext>();
            using var tenantScope = tenantContext.Push(envelope.TenantId.ToString("D"));
            await DispatchPayloadAsync(scope.ServiceProvider, envelope, cancellationToken);
            await outboxStore.MarkPublishedAsync(envelope.OutboxId, cancellationToken);
            metrics.Increment(
                "orchestrator_outbox_dispatch_total",
                ("effect", envelope.EffectType),
                ("outcome", "published"));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            var retryDelay = TimeSpan.FromSeconds(Math.Min(300, Math.Pow(2, Math.Min(envelope.AttemptCount, 8))));
            await outboxStore.MarkFailedAsync(
                envelope.OutboxId,
                ex.GetType().Name,
                retryDelay,
                CancellationToken.None);
            metrics.Increment(
                "orchestrator_outbox_dispatch_total",
                ("effect", envelope.EffectType),
                ("outcome", "failed"));
            logger.LogError(
                ex,
                "Failed to dispatch outbox effect {EffectType} for tenant {TenantId} message {MessageId}; retry in {RetrySeconds}s",
                envelope.EffectType,
                envelope.TenantId,
                envelope.MessageId,
                retryDelay.TotalSeconds);
        }
    }

    private static async Task DispatchPayloadAsync(
        IServiceProvider services,
        OutboxEnvelope envelope,
        CancellationToken cancellationToken)
    {
        switch (envelope.EffectType)
        {
            case OutboxEffectTypes.MemoryAppendMessage:
            {
                var payload = Deserialize<MemoryAppendMessageEffect>(envelope.Payload);
                await services.GetRequiredService<IConversationMemoryClient>().AppendMessageAsync(
                    payload.ConversationId,
                    payload.Role,
                    payload.Text,
                    payload.ExternalMessageId,
                    cancellationToken);
                return;
            }
            case OutboxEffectTypes.MemorySaveSession:
            {
                var payload = Deserialize<MemorySaveSessionEffect>(envelope.Payload);
                var stage = Enum.TryParse<JourneyStage>(payload.JourneyStage, true, out var parsed)
                    ? parsed
                    : JourneyStage.Started;
                await services.GetRequiredService<IConversationMemoryClient>().SaveSessionAsync(
                    new ConversationSession
                    {
                        ConversationId = payload.ConversationId,
                        CreatedAt = payload.CreatedAt,
                        LastMessageAt = payload.LastMessageAt,
                        JourneyStage = stage,
                        LastIntent = payload.LastIntent
                    },
                    cancellationToken);
                return;
            }
            case OutboxEffectTypes.ChannelReply:
            {
                var payload = Deserialize<ChannelReplyEffect>(envelope.Payload);
                await services.GetRequiredService<IChannelReplyClient>().SendReplyAsync(
                    payload.ConversationId,
                    payload.ReplyText,
                    envelope.IdempotencyKey,
                    cancellationToken);
                return;
            }
            case OutboxEffectTypes.HandoffRequest:
            {
                var payload = Deserialize<HandoffRequestEffect>(envelope.Payload);
                await services.GetRequiredService<IHandoffServiceClient>().RequestHandoffAsync(
                    new HandoffRequest
                    {
                        ConversationId = payload.ConversationId,
                        Reason = payload.Reason
                    },
                    envelope.IdempotencyKey,
                    cancellationToken);
                return;
            }
            case OutboxEffectTypes.AuditRecord:
            {
                var payload = Deserialize<AuditRecordEffect>(envelope.Payload);
                await services.GetRequiredService<IAuditServiceClient>().RecordJourneyEventAsync(
                    new JourneyAuditEvent
                    {
                        ConversationId = payload.ConversationId,
                        Intent = payload.Intent,
                        Outcome = payload.Outcome,
                        Timestamp = payload.Timestamp
                    },
                    envelope.IdempotencyKey,
                    cancellationToken);
                return;
            }
            case OutboxEffectTypes.IntentDetected:
            {
                var payload = Deserialize<IntentDetectedEffect>(envelope.Payload);
                await services.GetRequiredService<IConversationEventPublisher>().PublishIntentDetectedAsync(
                    new IntentDetectedEvent
                    {
                        ConversationId = payload.ConversationId,
                        Intent = payload.Intent,
                        Confidence = payload.Confidence,
                        DetectedAt = payload.DetectedAt
                    },
                    cancellationToken);
                return;
            }
            case OutboxEffectTypes.StateChanged:
            {
                var payload = Deserialize<StateChangedEffect>(envelope.Payload);
                await services.GetRequiredService<IConversationEventPublisher>().PublishConversationStateChangedAsync(
                    new ConversationStateChangedEvent
                    {
                        ConversationId = payload.ConversationId,
                        PreviousStage = payload.PreviousStage,
                        NewStage = payload.NewStage,
                        ChangedAt = payload.ChangedAt
                    },
                    cancellationToken);
                return;
            }
            default:
                throw new InvalidOperationException($"Unsupported outbox effect '{envelope.EffectType}'.");
        }
    }

    private static T Deserialize<T>(string payload) =>
        JsonSerializer.Deserialize<T>(payload)
        ?? throw new InvalidOperationException($"Outbox payload for {typeof(T).Name} is invalid.");
}
