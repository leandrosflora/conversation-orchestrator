namespace conversation_orchestrator.Models.Handoff;

public class HandoffRequest
{
    public required string ConversationId { get; init; }
    public required string Reason { get; init; }
}
