using Microsoft.Extensions.Options;
using conversation_orchestrator.Application.Ports.Outbound;
using conversation_orchestrator.Configuration;
using conversation_orchestrator.Platform;

namespace conversation_orchestrator.Adapters.Outbound.Http;

/// <summary>
/// Builds one IAgentRuntimeClient per configured skill, backed by a named HttpClient registered
/// in Program.cs (one per skill, with the same auth/resilience pipeline every other outbound
/// client gets). Client instances are cheap wrappers around IHttpClientFactory-managed
/// HttpClients, so constructing a new AgentRuntimeClient per Resolve() call is intentional, not
/// a missed-caching bug - IHttpClientFactory already owns the expensive parts (connection pooling).
/// </summary>
public sealed class AgentSkillRegistry(
    IHttpClientFactory httpClientFactory,
    IOptions<AgentSkillOptions> skillOptions,
    IOptions<TenantSkillOptions> tenantOptions,
    PlatformMetrics metrics,
    ILogger<AgentRuntimeClient> agentClientLogger) : IAgentSkillRegistry
{
    public static string HttpClientName(string skillId) => $"agent-skill:{skillId}";

    public IReadOnlyList<string> ResolveTenantSkills(string tenantId) =>
        tenantOptions.Value.Assignments.GetValueOrDefault(tenantId) ?? [];

    public IAgentRuntimeClient? Resolve(string skillId)
    {
        var configured = skillOptions.Value.Skills.Any(s => s.Id == skillId);
        if (!configured)
        {
            return null;
        }

        var httpClient = httpClientFactory.CreateClient(HttpClientName(skillId));
        return new AgentRuntimeClient(httpClient, metrics, agentClientLogger);
    }

    public IReadOnlyList<AgentSkillEntry> GetSkillEntries(IReadOnlyList<string> skillIds) =>
        skillOptions.Value.Skills.Where(s => skillIds.Contains(s.Id)).ToList();

    public string? ResolveSkillIdBySelectionButton(IReadOnlyList<string> skillIds, string buttonId) =>
        skillOptions.Value.Skills
            .FirstOrDefault(s => skillIds.Contains(s.Id) && s.SelectionButtonId == buttonId)
            ?.Id;
}
