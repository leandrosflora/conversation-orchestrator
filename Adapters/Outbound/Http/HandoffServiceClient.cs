using System.Net.Http.Json;
using conversation_orchestrator.Application.Ports.Outbound;
using conversation_orchestrator.Platform;

namespace conversation_orchestrator.Adapters.Outbound.Http;

public class HandoffServiceClient(
    HttpClient httpClient,
    PlatformMetrics metrics) : IHandoffServiceClient
{
    public async Task RequestHandoffAsync(
        HandoffRequest request,
        string idempotencyKey,
        CancellationToken cancellationToken)
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
        response.EnsureSuccessStatusCode();
    }
}
