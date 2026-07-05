using conversation_orchestrator.Models.Events;

namespace conversation_orchestrator.Events;

public interface IConversationEventPublisher
{
    Task PublishIntentDetectedAsync(IntentDetectedEvent evt, CancellationToken cancellationToken);

    Task PublishConversationStateChangedAsync(ConversationStateChangedEvent evt, CancellationToken cancellationToken);
}
