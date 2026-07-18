namespace conversation_orchestrator.Application.Ports.Outbound;

public sealed record OutboxEnvelope(
    Guid OutboxId,
    Guid TenantId,
    string MessageId,
    string ConversationId,
    string EffectType,
    string IdempotencyKey,
    string Payload,
    int AttemptCount);

public interface IOutboxStore
{
    Task<IReadOnlyList<OutboxEnvelope>> ClaimBatchAsync(
        int batchSize,
        TimeSpan lease,
        CancellationToken cancellationToken);

    Task MarkPublishedAsync(Guid outboxId, CancellationToken cancellationToken);

    Task MarkFailedAsync(
        Guid outboxId,
        string errorType,
        TimeSpan retryDelay,
        CancellationToken cancellationToken);
}
