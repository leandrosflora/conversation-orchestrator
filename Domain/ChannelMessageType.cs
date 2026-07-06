namespace conversation_orchestrator.Domain;

/// <summary>
/// Underlying integer values (Text=0, Interactive=1, Unsupported=2) must stay in this order —
/// they match whatsapp-bff's OrchestratorClient, which serializes this enum as a plain integer.
/// </summary>
public enum ChannelMessageType
{
    Text,
    Interactive,
    Unsupported
}
