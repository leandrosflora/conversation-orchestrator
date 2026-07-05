using conversation_orchestrator.AgentRuntime;
using conversation_orchestrator.Audit;
using conversation_orchestrator.ChannelBff;
using conversation_orchestrator.Events;
using conversation_orchestrator.Handoff;
using conversation_orchestrator.Models.Audit;
using conversation_orchestrator.Models.AgentRuntime;
using conversation_orchestrator.Models.Events;
using conversation_orchestrator.Models.Handoff;
using conversation_orchestrator.Models.Inbound;
using conversation_orchestrator.Sessions;

namespace conversation_orchestrator.Messages;

public static class MessageIngestionEndpoints
{
    private const string ProcessedStage = "processed";

    public static IEndpointRouteBuilder MapMessageIngestionEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/messages", HandleAsync);
        return endpoints;
    }

    private static async Task<IResult> HandleAsync(
        InboundChannelMessage message,
        IConversationSessionStore sessionStore,
        IAgentRuntimeClient agentRuntimeClient,
        IChannelReplyClient channelReplyClient,
        IConversationEventPublisher eventPublisher,
        IHandoffServiceClient handoffClient,
        IAuditServiceClient auditClient,
        ILogger<MessageIngestionLogCategory> logger,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(message.MessageId)
            || string.IsNullOrWhiteSpace(message.From)
            || string.IsNullOrWhiteSpace(message.ConversationId))
        {
            return Results.BadRequest(new { error = "MessageId, From, and ConversationId are required." });
        }

        var correlationId = Guid.NewGuid().ToString("n");
        using var scope = logger.BeginScope(new Dictionary<string, object>
        {
            ["CorrelationId"] = correlationId,
            ["ConversationId"] = message.ConversationId
        });

        var session = sessionStore.GetOrCreate(message.ConversationId);
        var previousStage = session.JourneyStage;

        var agentRequest = new AgentRuntimeRequest
        {
            ConversationId = message.ConversationId,
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
                    ConversationId = message.ConversationId,
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
                    ConversationId = message.ConversationId,
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
                    ConversationId = message.ConversationId,
                    Reason = result.HandoffReason ?? "unspecified"
                },
                cancellationToken);
        }
        else if (!string.IsNullOrWhiteSpace(result.ReplyText))
        {
            await channelReplyClient.SendReplyAsync(message.ConversationId, result.ReplyText, cancellationToken);
        }

        await auditClient.RecordJourneyEventAsync(
            new JourneyAuditEvent
            {
                ConversationId = message.ConversationId,
                Intent = result.Intent,
                Outcome = result.RequiresHandoff ? "handoff" : "processed",
                Timestamp = DateTimeOffset.UtcNow
            },
            cancellationToken);

        logger.LogInformation(
            "Processed message {MessageId} for conversation {ConversationId}: outcome={Outcome}",
            message.MessageId,
            message.ConversationId,
            result.RequiresHandoff ? "handoff" : "processed");

        return Results.Accepted();
    }
}

public sealed class MessageIngestionLogCategory;
