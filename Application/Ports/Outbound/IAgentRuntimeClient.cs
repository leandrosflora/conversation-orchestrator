using System.Text.Json;

namespace conversation_orchestrator.Application.Ports.Outbound;

public interface IAgentRuntimeClient
{
    /// <summary>Never throws; returns <see cref="AgentRuntimeResult.Unavailable"/> if the Agent Runtime cannot be reached.</summary>
    Task<AgentRuntimeResult> ProcessAsync(AgentRuntimeRequest request, CancellationToken cancellationToken);
}

/// <summary>
/// Skill-agnostic wire contract to whichever Agent Runtime agent-skill-registry resolves for the
/// conversation. State/StructuredState are opaque to the orchestrator - see journey-state-machine.
/// ExplicitConfirmationMessageId (renegotiation-specific customer-text judgment) is deliberately
/// absent - that judgment now lives in the agent, which already sees the raw message text.
/// </summary>
public class AgentRuntimeRequest
{
    public required string TenantId { get; init; }
    public required string ConversationId { get; init; }
    public required string MessageId { get; init; }
    public required string MessageType { get; init; }
    public string? Text { get; init; }
    public string? State { get; init; }
    public long JourneyVersion { get; init; }
    public string? LastIntent { get; init; }
    public JsonDocument? StructuredState { get; init; }
    /// <summary>True only on the turn the 15-minute session window was just found expired and
    /// reset - lets the agent tell the customer explicitly instead of silently re-asking for
    /// identification as if nothing had happened. See journey-state-machine's session-window
    /// requirement.</summary>
    public bool SessionReset { get; init; }
    /// <summary>Start of the current 15-minute session window (bumped to now on reset) - the
    /// agent uses this to discard any conversation history from before the window started, so a
    /// reset conversation can't fall back on identity/context the customer provided in an expired
    /// session.</summary>
    public DateTimeOffset? SessionStartedAt { get; init; }
}

public class AgentRuntimeResult
{
    public const string AgentRuntimeUnavailableReason = "agent_runtime_unavailable";

    /// <summary>No skill was assigned to the tenant, or the assigned/pinned skill id isn't (or
    /// is no longer) in the configured agent-skill-registry list.</summary>
    public const string SkillNotConfiguredReason = "skill_not_configured";

    public string? Intent { get; init; }
    public double Confidence { get; init; }
    public string? ReplyText { get; init; }
    public required bool RequiresHandoff { get; init; }
    public string? HandoffReason { get; init; }
    public string? State { get; init; }
    public JsonDocument? StructuredState { get; init; }

    public static AgentRuntimeResult Unavailable() => new()
    {
        Intent = null,
        Confidence = 0,
        ReplyText = null,
        RequiresHandoff = true,
        HandoffReason = AgentRuntimeUnavailableReason
    };

    public static AgentRuntimeResult SkillNotConfigured() => new()
    {
        Intent = null,
        Confidence = 0,
        ReplyText = null,
        RequiresHandoff = true,
        HandoffReason = SkillNotConfiguredReason
    };
}
