using conversation_orchestrator.Models.AgentRuntime;

namespace conversation_orchestrator.AgentRuntime;

public interface IAgentRuntimeClient
{
    /// <summary>Never throws; returns <see cref="AgentRuntimeResult.Unavailable"/> if the Agent Runtime cannot be reached.</summary>
    Task<AgentRuntimeResult> ProcessAsync(AgentRuntimeRequest request, CancellationToken cancellationToken);
}
