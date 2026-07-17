using conversation_orchestrator.Domain;

namespace conversation_orchestrator.Application.Ports.Outbound;

public interface IConversationMemoryClient
{
    /// <summary>Returns the existing session for the conversation, or a freshly-created one if none
    /// exists or conversation-memory-service cannot be reached.</summary>
    Task<ConversationSession> GetOrCreateSessionAsync(string conversationId, CancellationToken cancellationToken);

    /// <summary>Persists the session's current state. Best-effort - never throws.</summary>
    Task SaveSessionAsync(ConversationSession session, CancellationToken cancellationToken);

    /// <summary>Appends a message to the conversation's durable history. Best-effort - never throws.</summary>
    Task AppendMessageAsync(
        string conversationId, string role, string text, string? externalMessageId, CancellationToken cancellationToken);
}
