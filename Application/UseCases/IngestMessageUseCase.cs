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
            logger.LogInformation(
                "Message {MessageId} for conversation {ConversationId} was already completed",
                messageId,
                conversationId);
            return IngestMessageResult.AlreadyCompleted;
        }

        if (acquireResult == InboxAcquireResult.InProgress)
        {
            metrics.Increment("orchestrator_inbox_duplicates_total", ("state", "processing"));
            logger.LogInformation(
                "Message {MessageId} for conversation {ConversationId} is already being processed",
                messageId,
                conversationId);
            return IngestMessageResult.InProgress;
        }

        metrics.Increment("orchestrator_inbox_acquisitions_total", ("outcome", "acquired"));

        try
        {
            using (var cts = new CancellationTokenSource(SideEffectCallTimeout))
            {
                await conversationMemoryClient.AppendMessageAsync(
                    conversationId,
                    "user",
                    message.Text ?? string.Empty,
                    messageId,
                    cts.Token);
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
                    JourneyStage = session.JourneyStage.ToString(),
                    LastIntent = session.LastIntent
                },
                cancellationToken);

            metrics.Increment(
                "orchestrator_agent_decisions_total",
                ("outcome", ClassifyAgentOutcome(result)));

            if (result.Intent is not null)
            {
                session.LastIntent = result.Intent;

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

            if (result.RequiresHandoff)
            {
                session.JourneyStage = JourneyStage.HandoffRequested;
            }
            else
            {
                var trigger = JourneyTriggerClassifier.Classify(result.Intent);
                if (JourneyStageTransitions.TryGetNext(session.JourneyStage, trigger, out var nextStage))
                {
                    session.JourneyStage = nextStage;
                    metrics.Increment(
                        "orchestrator_journey_transitions_total",
                        ("from", previousStage.ToString()),
                        ("to", nextStage.ToString()),
                        ("outcome", "applied"));
                }
                else if (trigger != JourneyTrigger.None)
                {
                    metrics.Increment(
                        "orchestrator_journey_transitions_total",
                        ("from", session.JourneyStage.ToString()),
                        ("to", trigger.ToString()),
                        ("outcome", "rejected"));
                    logger.LogInformation(
                        "Rejected journey trigger {Trigger} from stage {Stage} for conversation {ConversationId}: not a legal transition",
                        trigger,
                        session.JourneyStage,
                        conversationId);
                }
            }

            if (session.JourneyStage != previousStage)
            {
                if (result.RequiresHandoff)
                {
                    metrics.Increment(
                        "orchestrator_journey_transitions_total",
                        ("from", previousStage.ToString()),
                        ("to", session.JourneyStage.ToString()),
                        ("outcome", "handoff"));
                }

                await eventPublisher.PublishConversationStateChangedAsync(
                    new ConversationStateChangedEvent
                    {
                        ConversationId = conversationId,
                        PreviousStage = previousStage.ToString(),
                        NewStage = session.JourneyStage.ToString(),
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

                metrics.Increment(
                    "orchestrator_handoffs_total",
                    ("reason", NormalizeHandoffReason(result.HandoffReason)));
            }
            else if (!string.IsNullOrWhiteSpace(result.ReplyText))
            {
                await channelReplyClient.SendReplyAsync(conversationId, result.ReplyText, cancellationToken);

                using var cts = new CancellationTokenSource(SideEffectCallTimeout);
                await conversationMemoryClient.AppendMessageAsync(
                    conversationId,
                    "assistant",
                    result.ReplyText,
                    externalMessageId: null,
                    cts.Token);
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
            metrics.Increment("orchestrator_journey_outcomes_total", ("outcome", outcome));

            logger.LogInformation(
                "Processed message {MessageId} for tenant {TenantId} conversation {ConversationId}: outcome={Outcome} stage={JourneyStage}",
                messageId,
                tenantId,
                conversationId,
                outcome,
                session.JourneyStage);

            return IngestMessageResult.Accepted;
        }
        catch (Exception ex)
        {
            metrics.Increment(
                "orchestrator_processing_failures_total",
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
            metrics.Observe(
                "orchestrator_message_processing_duration_seconds",
                stopwatch.Elapsed.TotalSeconds);
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

    private static string ClassifyAgentOutcome(AgentRuntimeResult result)
    {
        if (result.HandoffReason == AgentRuntimeResult.AgentRuntimeUnavailableReason)
        {
            return "unavailable";
        }

        return result.RequiresHandoff ? "handoff" : "automatic";
    }

    private static string NormalizeHandoffReason(string? reason) =>
        reason == AgentRuntimeResult.AgentRuntimeUnavailableReason
            ? "agent_runtime_unavailable"
            : "agent_decision";
}
