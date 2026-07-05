namespace conversation_orchestrator.Models.Audit;

public class JourneyAuditEvent
{
    public required string ConversationId { get; init; }
    public string? Intent { get; init; }
    public required string Outcome { get; init; }
    public required DateTimeOffset Timestamp { get; init; }
}
