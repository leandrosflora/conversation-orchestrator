namespace conversation_orchestrator.Application.Ports.Outbound;

public interface IAgentRuntimeClient
{
    /// <summary>Never throws; returns <see cref="AgentRuntimeResult.Unavailable"/> if the Agent Runtime cannot be reached.</summary>
    Task<AgentRuntimeResult> ProcessAsync(AgentRuntimeRequest request, CancellationToken cancellationToken);
}

public class AgentRuntimeRequest
{
    public required string ConversationId { get; init; }
    public required string MessageType { get; init; }
    public string? Text { get; init; }
    public string? JourneyStage { get; init; }
    public string? LastIntent { get; init; }
}

public class AgentRuntimeResult
{
    public const string AgentRuntimeUnavailableReason = "agent_runtime_unavailable";

    public string? Intent { get; init; }
    public double Confidence { get; init; }
    public string? ReplyText { get; init; }
    public required bool RequiresHandoff { get; init; }
    public string? HandoffReason { get; init; }

    /// <summary>Sentinel result used when the Agent Runtime is unreachable after retries are exhausted.</summary>
    public static AgentRuntimeResult Unavailable() => new()
    {
        Intent = null,
        Confidence = 0,
        ReplyText = null,
        RequiresHandoff = true,
        HandoffReason = AgentRuntimeUnavailableReason
    };
}
