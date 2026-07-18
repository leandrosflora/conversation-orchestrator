namespace conversation_orchestrator.Domain;

public class ConversationSession
{
    public required string ConversationId { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset LastMessageAt { get; set; }
    public JourneyStage JourneyStage { get; set; } = JourneyStage.Started;
    public string? LastIntent { get; set; }
}
