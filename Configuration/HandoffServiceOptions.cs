namespace conversation_orchestrator.Configuration;

public class HandoffServiceOptions
{
    public const string SectionName = "HandoffService";

    public string BaseUrl { get; set; } = string.Empty;
}
