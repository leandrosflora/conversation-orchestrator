namespace conversation_orchestrator.Configuration;

/// <summary>
/// Replaces the single-endpoint AgentRuntimeOptions: a configuration-driven list of agent
/// skills (renegotiation today; PIX, recarga, seguros in the future), each with its own Agent
/// Runtime base URL. Adding a skill is a config change, never an orchestrator code change - see
/// the agent-skill-registry capability.
/// </summary>
public class AgentSkillOptions
{
    public const string SectionName = "AgentSkills";

    public List<AgentSkillEntry> Skills { get; set; } = [];
}

public class AgentSkillEntry
{
    public string Id { get; set; } = string.Empty;
    public string BaseUrl { get; set; } = string.Empty;

    /// <summary>
    /// The downstream service's actual registered name for internal-auth signing (per-pair
    /// secret lookup, e.g. INTERNAL_AUTH_SECRET_CONVERSATION_ORCHESTRATOR__&lt;name&gt;) - not
    /// necessarily the same as the short business-facing Id. Defaults to Id when unset, which is
    /// only correct if the two happen to match.
    /// </summary>
    public string? DownstreamServiceName { get; set; }

    /// <summary>
    /// The WhatsApp interactive-button id/label used in the flow-selection menu shown when a
    /// tenant has more than one assigned skill and none is pinned yet. Unused (may stay unset)
    /// for a tenant with only one assigned skill, since that skill auto-pins with no menu shown.
    /// </summary>
    public string? SelectionButtonId { get; set; }
    public string? SelectionButtonTitle { get; set; }
}

/// <summary>
/// Maps a tenant id to the skill ids assigned to it, in menu-display order. A single assigned
/// skill auto-pins with no menu shown, preserving today's UX. Two or more trigger the
/// flow-selection menu on a session with no skill pinned yet - see agent-skill-registry.
/// </summary>
public class TenantSkillOptions
{
    public const string SectionName = "TenantSkillAssignments";

    public Dictionary<string, List<string>> Assignments { get; set; } = new();
}
