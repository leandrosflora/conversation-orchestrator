namespace conversation_orchestrator.Application.Ports.Outbound;

/// <summary>
/// Resolves which skill's Agent Runtime handles a conversation and provides the client to call
/// it. See the agent-skill-registry capability - conversation-orchestrator has no compiled
/// knowledge of what any skill's id or journey means, only that a skill id resolves to an
/// endpoint.
/// </summary>
public interface IAgentSkillRegistry
{
    /// <summary>Null if the tenant has no configured skill assignment.</summary>
    string? ResolveTenantSkill(string tenantId);

    /// <summary>Null if the skill id isn't in the configured skill list.</summary>
    IAgentRuntimeClient? Resolve(string skillId);
}
