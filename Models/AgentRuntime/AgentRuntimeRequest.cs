namespace conversation_orchestrator.Models.AgentRuntime;

public class AgentRuntimeRequest
{
    public required string ConversationId { get; init; }
    public required string MessageType { get; init; }
    public string? Text { get; init; }
    public string? JourneyStage { get; init; }
    public string? LastIntent { get; init; }
}
