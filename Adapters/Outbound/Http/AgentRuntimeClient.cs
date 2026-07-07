using System.Net.Http.Json;
using System.Text.Json;
using conversation_orchestrator.Application.Ports.Outbound;

namespace conversation_orchestrator.Adapters.Outbound.Http;

public class AgentRuntimeClient(HttpClient httpClient, ILogger<AgentRuntimeClient> logger) : IAgentRuntimeClient
{
    // PostAsJsonAsync with no explicit options serializes with System.Net.Http.Json's own
    // camelCase-by-default policy - not JsonSerializerOptions.Default's PascalCase - so it
    // silently sent "conversationId" instead of "ConversationId". agent-runtime-renegotiation's
    // Pydantic model only accepts the exact PascalCase alias (see its models.py comment), so
    // every request was rejected with 422 before ever reaching the agent.
    private static readonly JsonSerializerOptions RequestSerializerOptions = JsonSerializerOptions.Default;

    public async Task<AgentRuntimeResult> ProcessAsync(AgentRuntimeRequest request, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await httpClient.PostAsJsonAsync("/process", request, RequestSerializerOptions, cancellationToken);

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
