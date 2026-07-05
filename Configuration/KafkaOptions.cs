namespace conversation_orchestrator.Configuration;

public class KafkaOptions
{
    public const string SectionName = "Kafka";

    public string BootstrapServers { get; set; } = string.Empty;
    public string IntentDetectedTopic { get; set; } = string.Empty;
    public string ConversationStateChangedTopic { get; set; } = string.Empty;
}
