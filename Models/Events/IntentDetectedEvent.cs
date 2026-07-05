namespace conversation_orchestrator.Models.Events;

public class IntentDetectedEvent
{
    public required string ConversationId { get; init; }
    public required string Intent { get; init; }
    public double Confidence { get; init; }
    public required DateTimeOffset DetectedAt { get; init; }
}
