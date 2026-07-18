using System.Diagnostics;
using conversation_orchestrator.Application.Ports.Inbound;
using conversation_orchestrator.Application.Ports.Outbound;
using conversation_orchestrator.Domain;
using conversation_orchestrator.Platform;

namespace conversation_orchestrator.Application.UseCases;

public class IngestMessageUseCase(
    IConversationMemoryClient conversationMemoryClient,
    IMessageInboxStore inboxStore,
    IAgentRuntimeClient agentRuntimeClient,
    IChannelReplyClient channelReplyClient,
    IConversationEventPublisher eventPublisher,
    IHandoffServiceClient handoffClient,
    IAuditServiceClient auditClient,
    TenantContext tenantContext,
    PlatformMetrics metrics,
    ILogger<IngestMessageUseCase> logger) : IIngestMessageUseCase
{
    private const string ProcessedStage = "processed";
    private static readonly TimeSpan SideEffectCallTimeout = TimeSpan.FromSeconds(5);

    public async Task<IngestMessageResult> ExecuteAsync(
        InboundChannelMessage message,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        var messageId = message.MessageId!;
        var conversationId = message.ConversationId!;
        var tenantId = tenantContext.TenantId;

        var acquireResult = await inboxStore.TryAcquireAsync(messageId, conversationId, cancellationToken);
        if (acquireResult == InboxAcquireResult.Completed)
        {
            metrics.Increment("orchestrator_inbox_duplicates_total", ("state", "completed"));
            return IngestMessageResult.AlreadyCompleted;
        }
        if (acquireResult == InboxAcquireResult.InProgress)
        {
            metrics.Increment("orchestrator_inbox_duplicates_total", ("state", "processing"));
            return IngestMessageResult.InProgress;
        }

        try
        {
            using (var cts = new CancellationTokenSource(SideEffectCallTimeout))
            {
                await conversationMemoryClient.AppendMessageAsync(
                    conversationId, "user", message.Text ?? string.Empty, messageId, cts.Token);
            }

            ConversationSession session;
            using (var cts = new CancellationTokenSource(SideEffectCallTimeout))
            {
                session = await conversationMemoryClient.GetOrCreateSessionAsync(conversationId, cts.Token);
            }

            var previousStage = session.JourneyStage;
            var result = await agentRuntimeClient.ProcessAsync(
                new AgentRuntimeRequest
                {
                    TenantId = tenantId,
                    ConversationId = conversationId,
                    MessageType = message.Type.ToString(),
                    Text = message.Text,
                    JourneyStage = session.JourneyStage,
                    LastIntent = session.LastIntent
                },
                cancellationToken);

            if (result.Intent is not null)
            {
                session.LastIntent = result.Intent;
                session.JourneyStage = ProcessedStage;
                await eventPublisher.PublishIntentDetectedAsync(
                    new IntentDetectedEvent
                    {
                        ConversationId = conversationId,
                        Intent = result.Intent,
                        Confidence = result.Confidence,
                        DetectedAt = DateTimeOffset.UtcNow
                    },
                    cancellationToken);
            }

            if (session.JourneyStage != previousStage)
            {
                await eventPublisher.PublishConversationStateChangedAsync(
                    new ConversationStateChangedEvent
                    {
                        ConversationId = conversationId,
                        PreviousStage = previousStage,
                        NewStage = session.JourneyStage,
                        ChangedAt = DateTimeOffset.UtcNow
                    },
                    cancellationToken);
            }

            using (var cts = new CancellationTokenSource(SideEffectCallTimeout))
            {
                await conversationMemoryClient.SaveSessionAsync(session, cts.Token);
            }

            var outcome = "processed";
            if (result.RequiresHandoff)
            {
                outcome = "handoff";
                await handoffClient.RequestHandoffAsync(
                    new HandoffRequest
                    {
                        ConversationId = conversationId,
                        Reason = result.HandoffReason ?? "unspecified"
                    },
                    $"handoff:{messageId}",
                    cancellationToken);
                metrics.Increment("orchestrator_handoffs_total",
                    ("reason", result.HandoffReason ?? "unspecified"));
            }
            else if (!string.IsNullOrWhiteSpace(result.ReplyText))
            {
                await channelReplyClient.SendReplyAsync(conversationId, result.ReplyText, cancellationToken);
                using var cts = new CancellationTokenSource(SideEffectCallTimeout);
                await conversationMemoryClient.AppendMessageAsync(
                    conversationId, "assistant", result.ReplyText, externalMessageId: null, cts.Token);
            }

            using (var cts = new CancellationTokenSource(SideEffectCallTimeout))
            {
                await auditClient.RecordJourneyEventAsync(
                    new JourneyAuditEvent
                    {
                        ConversationId = conversationId,
                        Intent = result.Intent,
                        Outcome = outcome,
                        Timestamp = DateTimeOffset.UtcNow
                    },
                    $"audit:{messageId}",
                    cts.Token);
            }

            await inboxStore.MarkCompletedAsync(messageId, CancellationToken.None);
            metrics.Increment("orchestrator_journey_outcomes_total",
                ("outcome", outcome), ("intent", result.Intent ?? "unknown"));
            return IngestMessageResult.Accepted;
        }
        catch (Exception ex)
        {
            metrics.Increment("orchestrator_processing_failures_total",
                ("exception", ex.GetType().Name));
            logger.LogError(
                ex,
                "Message {MessageId} for tenant {TenantId} conversation {ConversationId} failed before Inbox completion",
                messageId,
                tenantId,
                conversationId);
            await MarkFailedBestEffortAsync(messageId, ex.GetType().Name);
            throw;
        }
        finally
        {
            stopwatch.Stop();
            metrics.Observe("orchestrator_message_processing_duration_seconds", stopwatch.Elapsed.TotalSeconds);
        }
    }

    private async Task MarkFailedBestEffortAsync(string messageId, string errorType)
    {
        try
        {
            using var cts = new CancellationTokenSource(SideEffectCallTimeout);
            await inboxStore.MarkFailedAsync(messageId, errorType, cts.Token);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to mark Inbox message {MessageId} as failed", messageId);
        }
    }
}
