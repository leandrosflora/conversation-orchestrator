namespace conversation_orchestrator.Application.Ports.Outbound;

public interface IConversationEventPublisher
{
    Task PublishIntentDetectedAsync(IntentDetectedEvent evt, CancellationToken cancellationToken);

    Task PublishConversationStateChangedAsync(ConversationStateChangedEvent evt, CancellationToken cancellationToken);
}

public class IntentDetectedEvent
{
    public required string ConversationId { get; init; }
    public required string Intent { get; init; }
    public double Confidence { get; init; }
    public required DateTimeOffset DetectedAt { get; init; }
}

public class ConversationStateChangedEvent
{
    public required string ConversationId { get; init; }
    public required string PreviousStage { get; init; }
    public required string NewStage { get; init; }
    public required DateTimeOffset ChangedAt { get; init; }
}
