namespace conversation_orchestrator.Models.Session;

public class ConversationSession
{
    public required string ConversationId { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset LastMessageAt { get; set; }
    public string JourneyStage { get; set; } = "started";
    public string? LastIntent { get; set; }
}
