namespace conversation_orchestrator.Configuration;

public class ConversationMemoryOptions
{
    public const string SectionName = "ConversationMemory";

    public string BaseUrl { get; set; } = string.Empty;

    // Matches the demo tenant already seeded in database/conversational-ai-mongodb-init.js,
    // since this platform has no multi-tenancy concept anywhere else either.
    public string TenantId { get; set; } = "00000000-0000-0000-0000-000000000001";
}
