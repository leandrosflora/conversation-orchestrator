namespace conversation_orchestrator.Models.Events;

public class ConversationStateChangedEvent
{
    public required string ConversationId { get; init; }
    public required string PreviousStage { get; init; }
    public required string NewStage { get; init; }
    public required DateTimeOffset ChangedAt { get; init; }
}
