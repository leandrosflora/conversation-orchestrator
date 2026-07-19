using conversation_orchestrator.Domain;

namespace conversation_orchestrator.Application.Ports.Outbound;

public enum InboxAcquireResult
{
    Acquired,
    InProgress,
    Completed,
    Late
}

public sealed record ConversationCheckpoint(
    JourneyStage JourneyStage,
    string? LastIntent,
    long Version,
    DateTimeOffset? LastReceivedAt,
    string? LastMessageId);

public sealed record InboxLease(
    InboxAcquireResult Result,
    ConversationCheckpoint? Checkpoint = null);

public sealed record DurableEffect(
    string EffectType,
    string IdempotencyKey,
    string Payload);

public sealed record CompleteMessageCommand(
    Guid TenantId,
    string MessageId,
    string ConversationId,
    DateTimeOffset ReceivedAt,
    JourneyStage JourneyStage,
    string? LastIntent,
    long ExpectedVersion,
    IReadOnlyCollection<DurableEffect> Effects);

public interface IMessageInboxStore
{
    Task<InboxLease> TryAcquireAsync(
        Guid tenantId,
        string messageId,
        string conversationId,
        DateTimeOffset receivedAt,
        CancellationToken cancellationToken);

    Task CompleteAsync(
        CompleteMessageCommand command,
        CancellationToken cancellationToken);

    Task MarkFailedAsync(
        Guid tenantId,
        string messageId,
        string errorType,
        CancellationToken cancellationToken);
}
