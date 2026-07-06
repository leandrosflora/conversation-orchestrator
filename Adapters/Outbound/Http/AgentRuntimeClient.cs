using System.Net.Http.Json;
using conversation_orchestrator.Application.Ports.Outbound;

namespace conversation_orchestrator.Adapters.Outbound.Http;

public class AgentRuntimeClient(HttpClient httpClient, ILogger<AgentRuntimeClient> logger) : IAgentRuntimeClient
{
    public async Task<AgentRuntimeResult> ProcessAsync(AgentRuntimeRequest request, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await httpClient.PostAsJsonAsync("/process", request, cancellationToken);

            if (response.IsSuccessStatusCode)
            {
                var result = await response.Content.ReadFromJsonAsync<AgentRuntimeResult>(cancellationToken);
                if (result is not null)
                {
                    return result;
                }

                logger.LogWarning(
                    "Agent Runtime returned an empty response body for conversation {ConversationId}",
                    request.ConversationId);
                return AgentRuntimeResult.Unavailable();
            }

            logger.LogWarning(
                "Agent Runtime responded with non-success status {StatusCode} for conversation {ConversationId}",
                response.StatusCode,
                request.ConversationId);
            return AgentRuntimeResult.Unavailable();
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                ex,
                "Failed to reach the Agent Runtime for conversation {ConversationId} after retries",
                request.ConversationId);
            return AgentRuntimeResult.Unavailable();
        }
    }
}
