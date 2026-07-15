namespace conversation_orchestrator.Application.Ports.Outbound;

public interface IMessageDedupeStore
{
    /// <summary>Returns true the first time this messageId is seen, false on every subsequent call
    /// within the retention window - callers should treat false as "already processed, skip".</summary>
    bool TryMarkProcessed(string messageId);
}
