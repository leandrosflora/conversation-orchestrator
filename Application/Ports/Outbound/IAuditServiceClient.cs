namespace conversation_orchestrator.Application.Ports.Outbound;

public interface IAuditServiceClient
{
    /// <summary>Never throws; logs and returns on failure to reach the Audit Service.</summary>
    Task RecordJourneyEventAsync(JourneyAuditEvent auditEvent, CancellationToken cancellationToken);
}

public class JourneyAuditEvent
{
    public required string ConversationId { get; init; }
    public string? Intent { get; init; }
    public required string Outcome { get; init; }
    public required DateTimeOffset Timestamp { get; init; }
}
