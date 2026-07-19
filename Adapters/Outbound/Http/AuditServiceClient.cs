using System.Net.Http.Json;
using conversation_orchestrator.Application.Ports.Outbound;
using conversation_orchestrator.Platform;

namespace conversation_orchestrator.Adapters.Outbound.Http;

public class AuditServiceClient(
    HttpClient httpClient,
    PlatformMetrics metrics) : IAuditServiceClient
{
    public async Task RecordJourneyEventAsync(
        JourneyAuditEvent auditEvent,
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "/journey-events")
        {
            Content = JsonContent.Create(auditEvent)
        };
        httpRequest.Headers.TryAddWithoutValidation("Idempotency-Key", idempotencyKey);
        using var response = await httpClient.SendAsync(httpRequest, cancellationToken);
        metrics.Increment(
            "orchestrator_audit_events_total",
            ("outcome", response.IsSuccessStatusCode ? "success" : "downstream_error"));
        response.EnsureSuccessStatusCode();
    }
}
