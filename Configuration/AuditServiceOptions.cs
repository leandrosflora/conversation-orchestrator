namespace conversation_orchestrator.Configuration;

public class AuditServiceOptions
{
    public const string SectionName = "AuditService";

    public string BaseUrl { get; set; } = string.Empty;
}
