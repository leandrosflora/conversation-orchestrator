namespace conversation_orchestrator.Application.Ports.Outbound;

public enum InboxAcquireResult
{
    Acquired,
    InProgress,
    Completed,
    Late
}

/// <summary>
/// State is an opaque string whose vocabulary is owned entirely by the conversation's resolved
/// skill's agent - the orchestrator never interprets it, except for the one reserved value it
/// owns itself (see StartedState/HandoffRequestedState in IngestMessageUseCase). SkillId is null
/// until the conversation's first completed turn resolves and pins it (see agent-skill-registry).
/// StructuredState is raw JSON text (or null), round-tripped verbatim - never parsed here.
/// </summary>
public sealed record ConversationCheckpoint(
    string State,
    string? LastIntent,
    long Version,
    DateTimeOffset? LastReceivedAt,
    string? LastMessageId,
    string? SkillId = null,
    string? StructuredState = null)
{
    public const string StartedState = "Started";
}

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
    string State,
    string? LastIntent,
    long ExpectedVersion,
    IReadOnlyCollection<DurableEffect> Effects,
    string? SkillId = null,
    string? StructuredState = null);

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
