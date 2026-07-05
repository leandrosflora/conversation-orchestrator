using System.Net.Http.Json;
using conversation_orchestrator.Models.Audit;

namespace conversation_orchestrator.Audit;

public class AuditServiceClient(HttpClient httpClient, ILogger<AuditServiceClient> logger) : IAuditServiceClient
{
    public async Task RecordJourneyEventAsync(JourneyAuditEvent auditEvent, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await httpClient.PostAsJsonAsync("/journey-events", auditEvent, cancellationToken);

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
            logger.LogWarning(
                ex, "Failed to record journey audit event for conversation {ConversationId}", auditEvent.ConversationId);
        }
    }
}
