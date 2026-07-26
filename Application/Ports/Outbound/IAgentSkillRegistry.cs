using conversation_orchestrator.Configuration;

namespace conversation_orchestrator.Application.Ports.Outbound;

/// <summary>
/// Resolves which skill's Agent Runtime handles a conversation and provides the client to call
/// it. See the agent-skill-registry capability - conversation-orchestrator has no compiled
/// knowledge of what any skill's id or journey means, only that a skill id resolves to an
/// endpoint.
/// </summary>
public interface IAgentSkillRegistry
{
    /// <summary>Empty if the tenant has no configured skill assignment. One entry: that skill
    /// auto-pins with no menu. Two or more: the flow-selection menu is shown until one is
    /// chosen.</summary>
    IReadOnlyList<string> ResolveTenantSkills(string tenantId);

    /// <summary>Null if the skill id isn't in the configured skill list.</summary>
    IAgentRuntimeClient? Resolve(string skillId);

    /// <summary>The configured entries (for menu-building) among the given skill ids, in the
    /// same order.</summary>
    IReadOnlyList<AgentSkillEntry> GetSkillEntries(IReadOnlyList<string> skillIds);

    /// <summary>Resolves a WhatsApp interactive-button tap to a skill id, restricted to the
    /// given candidate skill ids. Null if the button id doesn't match any of them.</summary>
    string? ResolveSkillIdBySelectionButton(IReadOnlyList<string> skillIds, string buttonId);
}
