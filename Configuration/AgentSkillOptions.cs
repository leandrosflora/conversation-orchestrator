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
}

/// <summary>
/// Maps a tenant id to the skill id that handles its conversations. V1 is tenant-scoped only
/// (one skill per tenant) - see design.md's Open Questions for routing by channel identity once
/// a tenant needs more than one skill.
/// </summary>
public class TenantSkillOptions
{
    public const string SectionName = "TenantSkillAssignments";

    public Dictionary<string, string> Assignments { get; set; } = new();
}
