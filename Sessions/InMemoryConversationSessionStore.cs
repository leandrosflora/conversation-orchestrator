using System.Collections.Concurrent;
using Microsoft.Extensions.Options;
using conversation_orchestrator.Configuration;
using conversation_orchestrator.Models.Session;

namespace conversation_orchestrator.Sessions;

public class InMemoryConversationSessionStore(IOptions<ConversationSessionOptions> options, TimeProvider timeProvider)
    : IConversationSessionStore
{
    private readonly ConcurrentDictionary<string, ConversationSession> _sessions = new();
    private readonly TimeSpan _ttl = TimeSpan.FromMinutes(options.Value.TtlMinutes);

    public ConversationSession GetOrCreate(string conversationId)
    {
        var now = timeProvider.GetUtcNow();

        if (_sessions.TryGetValue(conversationId, out var existing) && now - existing.LastMessageAt <= _ttl)
        {
            existing.LastMessageAt = now;
            return existing;
        }

        var session = new ConversationSession
        {
            ConversationId = conversationId,
            CreatedAt = now,
            LastMessageAt = now
        };
        _sessions[conversationId] = session;
        return session;
    }
}
