using conversation_orchestrator.Application.Ports.Inbound;
using conversation_orchestrator.Application.Ports.Outbound;
using conversation_orchestrator.Domain;

namespace conversation_orchestrator.Application.UseCases;

public class IngestMessageUseCase(
    IConversationMemoryClient conversationMemoryClient,
    IMessageInboxStore inboxStore,
    IAgentRuntimeClient agentRuntimeClient,
    IChannelReplyClient channelReplyClient,
    IConversationEventPublisher eventPublisher,
    IHandoffServiceClient handoffClient,
    IAuditServiceClient auditClient,
    ILogger<IngestMessageUseCase> logger) : IIngestMessageUseCase
{
    private static readonly TimeSpan SideEffectCallTimeout = TimeSpan.FromSeconds(5);

    public async Task<IngestMessageResult> ExecuteAsync(
        InboundChannelMessage message,
        CancellationToken cancellationToken)
    {
        var messageId = message.MessageId!;
        var conversationId = message.ConversationId!;

        var acquireResult = await inboxStore.TryAcquireAsync(messageId, conversationId, cancellationToken);
        if (acquireResult == InboxAcquireResult.Completed)
        {
            logger.LogInformation(
                "Message {MessageId} for conversation {ConversationId} was already completed",
                messageId,
                conversationId);
            return IngestMessageResult.AlreadyCompleted;
        }

        if (acquireResult == InboxAcquireResult.InProgress)
        {
            logger.LogInformation(
                "Message {MessageId} for conversation {ConversationId} is already being processed",
                messageId,
                conversationId);
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
            var agentRequest = new AgentRuntimeRequest
            {
                ConversationId = conversationId,
                MessageType = message.Type.ToString(),
                Text = message.Text,
                JourneyStage = session.JourneyStage.ToString(),
                LastIntent = session.LastIntent
            };

            var result = await agentRuntimeClient.ProcessAsync(agentRequest, cancellationToken);

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

            // The Orchestrator, not the Agent Runtime, owns stage transitions: RequiresHandoff
            // always wins regardless of stage/trigger, and a classified trigger only applies
            // if the transition table says it's legal from the session's current stage - an
            // unrecognized or illegal trigger leaves JourneyStage unchanged rather than being
            // blindly applied (see openspec journey-state-machine capability).
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
                }
                else if (trigger != JourneyTrigger.None)
                {
                    logger.LogInformation(
                        "Rejected journey trigger {Trigger} from stage {Stage} for conversation {ConversationId}: not a legal transition",
                        trigger,
                        session.JourneyStage,
                        conversationId);
                }
            }

            if (session.JourneyStage != previousStage)
            {
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

            if (result.RequiresHandoff)
            {
                await handoffClient.RequestHandoffAsync(
                    new HandoffRequest
                    {
                        ConversationId = conversationId,
                        Reason = result.HandoffReason ?? "unspecified"
                    },
                    $"handoff:{messageId}",
                    cancellationToken);
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
                        Outcome = result.RequiresHandoff ? "handoff" : "processed",
                        Timestamp = DateTimeOffset.UtcNow
                    },
                    $"audit:{messageId}",
                    cts.Token);
            }

            await inboxStore.MarkCompletedAsync(messageId, CancellationToken.None);

            logger.LogInformation(
                "Processed message {MessageId} for conversation {ConversationId}: outcome={Outcome}",
                messageId,
                conversationId,
                result.RequiresHandoff ? "handoff" : "processed");

            return IngestMessageResult.Accepted;
        }
        catch (Exception ex)
        {
            logger.LogError(
                ex,
                "Message {MessageId} for conversation {ConversationId} failed before Inbox completion",
                messageId,
                conversationId);

            await MarkFailedBestEffortAsync(messageId, ex.GetType().Name);
            throw;
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
