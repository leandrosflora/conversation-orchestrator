using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;
using conversation_orchestrator.Application.Ports.Outbound;
using conversation_orchestrator.Platform;

namespace conversation_orchestrator.Adapters.Outbound.Http;

public class AgentRuntimeClient(
    HttpClient httpClient,
    PlatformMetrics metrics,
    ILogger<AgentRuntimeClient> logger) : IAgentRuntimeClient
{
    private static readonly JsonSerializerOptions RequestSerializerOptions = JsonSerializerOptions.Default;

    public async Task<AgentRuntimeResult> ProcessAsync(
        AgentRuntimeRequest request,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        var outcome = "exception";

        try
        {
            using var response = await httpClient.PostAsJsonAsync(
                "/process",
                request,
                RequestSerializerOptions,
                cancellationToken);

            if (response.IsSuccessStatusCode)
            {
                var result = await response.Content.ReadFromJsonAsync<AgentRuntimeResult>(cancellationToken);
                if (result is not null)
                {
                    outcome = result.RequiresHandoff ? "handoff" : "success";
                    return result;
                }

                outcome = "empty_response";
                logger.LogWarning(
                    "Agent Runtime returned an empty response body for conversation {ConversationId}",
                    request.ConversationId);
                return AgentRuntimeResult.Unavailable();
            }

            outcome = "downstream_error";
            logger.LogWarning(
                "Agent Runtime responded with non-success status {StatusCode} for conversation {ConversationId}",
                response.StatusCode,
                request.ConversationId);
            return AgentRuntimeResult.Unavailable();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            outcome = "cancelled";
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                ex,
                "Failed to reach the Agent Runtime for conversation {ConversationId}",
                request.ConversationId);
            return AgentRuntimeResult.Unavailable();
        }
        finally
        {
            stopwatch.Stop();
            metrics.Increment("orchestrator_agent_runtime_calls_total", ("outcome", outcome));
            metrics.Observe(
                "orchestrator_agent_runtime_call_duration_seconds",
                stopwatch.Elapsed.TotalSeconds,
                ("outcome", outcome));
        }
    }
}
