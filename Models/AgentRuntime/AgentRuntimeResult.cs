namespace conversation_orchestrator.Models.AgentRuntime;

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
