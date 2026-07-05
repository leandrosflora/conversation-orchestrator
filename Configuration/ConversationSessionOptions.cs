namespace conversation_orchestrator.Configuration;

public class ConversationSessionOptions
{
    public const string SectionName = "Session";

    public int TtlMinutes { get; set; } = 30;
}
