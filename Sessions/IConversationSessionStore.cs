using conversation_orchestrator.Models.Session;

namespace conversation_orchestrator.Sessions;

public interface IConversationSessionStore
{
    /// <summary>Returns the existing session for the conversation if it hasn't expired, otherwise creates a new one.</summary>
    ConversationSession GetOrCreate(string conversationId);
}
