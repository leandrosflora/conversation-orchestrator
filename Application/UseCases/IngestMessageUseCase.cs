using conversation_orchestrator.Application.Ports.Inbound;
using conversation_orchestrator.Application.Ports.Outbound;
using conversation_orchestrator.Domain;

namespace conversation_orchestrator.Application.UseCases;

public class IngestMessageUseCase(
    IConversationSessionStore sessionStore,
    IAgentRuntimeClient agentRuntimeClient,
    IChannelReplyClient channelReplyClient,
    IConversationEventPublisher eventPublisher,
    IHandoffServiceClient handoffClient,
    IAuditServiceClient auditClient,
    ILogger<IngestMessageUseCase> logger) : IIngestMessageUseCase
{
    private const string ProcessedStage = "processed";

    public async Task ExecuteAsync(InboundChannelMessage message, CancellationToken cancellationToken)
    {
        var conversationId = message.ConversationId!;
        var session = sessionStore.GetOrCreate(conversationId);
        var previousStage = session.JourneyStage;

        var agentRequest = new AgentRuntimeRequest
        {
            ConversationId = conversationId,
            MessageType = message.Type.ToString(),
            Text = message.Text,
            JourneyStage = session.JourneyStage,
            LastIntent = session.LastIntent
        };

        var result = await agentRuntimeClient.ProcessAsync(agentRequest, cancellationToken);

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

        if (result.RequiresHandoff)
        {
            await handoffClient.RequestHandoffAsync(
                new HandoffRequest
                {
                    ConversationId = conversationId,
                    Reason = result.HandoffReason ?? "unspecified"
                },
                cancellationToken);
        }
        else if (!string.IsNullOrWhiteSpace(result.ReplyText))
        {
            await channelReplyClient.SendReplyAsync(conversationId, result.ReplyText, cancellationToken);
        }

        await auditClient.RecordJourneyEventAsync(
            new JourneyAuditEvent
            {
                ConversationId = conversationId,
                Intent = result.Intent,
                Outcome = result.RequiresHandoff ? "handoff" : "processed",
                Timestamp = DateTimeOffset.UtcNow
            },
            cancellationToken);

        logger.LogInformation(
            "Processed message {MessageId} for conversation {ConversationId}: outcome={Outcome}",
            message.MessageId,
            conversationId,
            result.RequiresHandoff ? "handoff" : "processed");
    }
}
