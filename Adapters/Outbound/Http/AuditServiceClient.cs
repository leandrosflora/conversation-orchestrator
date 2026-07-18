using System.Net.Http.Json;
using conversation_orchestrator.Application.Ports.Outbound;
using conversation_orchestrator.Platform;

namespace conversation_orchestrator.Adapters.Outbound.Http;

public class AuditServiceClient(
    HttpClient httpClient,
    PlatformMetrics metrics,
    ILogger<AuditServiceClient> logger) : IAuditServiceClient
{
    public async Task RecordJourneyEventAsync(
        JourneyAuditEvent auditEvent,
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        try
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

            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning(
                    "Audit Service responded with non-success status {StatusCode} for conversation {ConversationId}",
                    response.StatusCode,
                    auditEvent.ConversationId);
            }
        }
        catch (Exception ex)
        {
            metrics.Increment("orchestrator_audit_events_total", ("outcome", "exception"));
            logger.LogWarning(
                ex,
                "Failed to record journey audit event for conversation {ConversationId}",
                auditEvent.ConversationId);
        }
    }
}
