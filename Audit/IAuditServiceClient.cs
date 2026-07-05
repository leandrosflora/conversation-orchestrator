using conversation_orchestrator.Models.Audit;

namespace conversation_orchestrator.Audit;

public interface IAuditServiceClient
{
    /// <summary>Never throws; logs and returns on failure to reach the Audit Service.</summary>
    Task RecordJourneyEventAsync(JourneyAuditEvent auditEvent, CancellationToken cancellationToken);
}
