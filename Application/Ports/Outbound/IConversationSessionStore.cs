using conversation_orchestrator.Domain;

namespace conversation_orchestrator.Application.Ports.Outbound;

public interface IConversationSessionStore
{
    /// <summary>Returns the existing session for the conversation if it hasn't expired, otherwise creates a new one.</summary>
    ConversationSession GetOrCreate(string conversationId);
}
