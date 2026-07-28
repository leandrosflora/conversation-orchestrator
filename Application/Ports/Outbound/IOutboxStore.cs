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

    /// <summary>Waits until a new effect is available or the timeout elapses. Returns true if woken by a signal, false on timeout.</summary>
    Task<bool> WaitForPendingEffectAsync(TimeSpan timeout, CancellationToken cancellationToken);

    /// <summary>
    /// Age of the oldest outbox effect that is still expected to resolve on its own (pending,
    /// currently publishing, or failed-but-retrying-soon) - excludes effects already parked as
    /// terminal dead letters, which have their own signal. Null when nothing is unresolved.
    /// Backs a staleness gauge so a dispatcher that has gone silent (e.g. every candidate is
    /// blocked, or claiming nothing for any other reason) surfaces as a metric instead of only
    /// as a customer never getting a reply.
    /// </summary>
    Task<TimeSpan?> GetOldestUnresolvedEffectAgeAsync(CancellationToken cancellationToken);
}
