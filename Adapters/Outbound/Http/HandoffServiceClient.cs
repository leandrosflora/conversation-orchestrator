using System.Net.Http.Json;
using conversation_orchestrator.Application.Ports.Outbound;
using conversation_orchestrator.Platform;

namespace conversation_orchestrator.Adapters.Outbound.Http;

public class HandoffServiceClient(
    HttpClient httpClient,
    PlatformMetrics metrics,
    ILogger<HandoffServiceClient> logger) : IHandoffServiceClient
{
    public async Task RequestHandoffAsync(
        HandoffRequest request,
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        try
        {
            using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "/handoffs")
            {
                Content = JsonContent.Create(request)
            };
            httpRequest.Headers.TryAddWithoutValidation("Idempotency-Key", idempotencyKey);

            using var response = await httpClient.SendAsync(httpRequest, cancellationToken);
            metrics.Increment(
                "orchestrator_handoff_requests_total",
                ("outcome", response.IsSuccessStatusCode ? "success" : "downstream_error"));

            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning(
                    "Handoff Service responded with non-success status {StatusCode} for conversation {ConversationId}",
                    response.StatusCode,
                    request.ConversationId);
            }
        }
        catch (Exception ex)
        {
            metrics.Increment("orchestrator_handoff_requests_total", ("outcome", "exception"));
            logger.LogWarning(
                ex,
                "Failed to request handoff for conversation {ConversationId}",
                request.ConversationId);
        }
    }
}
