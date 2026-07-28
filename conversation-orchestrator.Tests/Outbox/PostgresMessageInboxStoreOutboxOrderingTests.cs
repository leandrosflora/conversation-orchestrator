using Microsoft.Extensions.Options;
using Npgsql;
using Testcontainers.PostgreSql;
using conversation_orchestrator.Adapters.Outbound.Persistence;
using conversation_orchestrator.Application.Outbox;
using conversation_orchestrator.Application.Ports.Outbound;
using conversation_orchestrator.Configuration;
using Xunit;

namespace conversation_orchestrator.Tests.Outbox;

/// <summary>
/// Regression coverage for the outbox dispatch-ordering bug found during the 2026-07-20 E2E
/// validation: a turn whose channel.reply got permanently parked (status='failed',
/// next_attempt_at pushed ~10 years out by OutboxDispatcherService.ParkedRetryDelay) must not
/// block every later journey_version of the same conversation forever. A predecessor that is
/// still genuinely scheduled to retry soon must keep blocking, so ordering is only relaxed for
/// terminal failures.
/// </summary>
public sealed class PostgresMessageInboxStoreOutboxOrderingTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder()
        .WithImage("postgres:16-alpine")
        .WithDatabase("conversational_ai")
        .WithUsername("postgres")
        .WithPassword("postgres")
        .Build();

    private static readonly TimeSpan ParkedRetryDelay = TimeSpan.FromDays(3650);
    private NpgsqlDataSource _dataSource = null!;
    private PostgresMessageInboxStore _store = null!;

    public async Task InitializeAsync()
    {
        await _container.StartAsync();
        _dataSource = NpgsqlDataSource.Create(_container.GetConnectionString());
        _store = new PostgresMessageInboxStore(
            _dataSource,
            Options.Create(new PostgresOptions { InboxLeaseSeconds = 300 }));
    }

    public async Task DisposeAsync()
    {
        await _dataSource.DisposeAsync();
        await _container.DisposeAsync();
    }

    [Fact]
    public async Task ParkedPredecessor_DoesNotBlockLaterJourneyVersion()
    {
        var tenantId = Guid.NewGuid();
        const string conversationId = "conv-parked";

        var turn2OutboxId = await RunTwoTurnsAsync(
            tenantId,
            conversationId,
            afterTurn1Claimed: async envelope =>
                await _store.MarkFailedAsync(
                    envelope.OutboxId, "NonRetryableDispatchException", ParkedRetryDelay, CancellationToken.None));

        var claimed = await _store.ClaimBatchAsync(10, TimeSpan.FromSeconds(90), CancellationToken.None);

        Assert.Contains(claimed, e => e.OutboxId == turn2OutboxId);
    }

    [Fact]
    public async Task StillRetryingPredecessor_KeepsBlockingLaterJourneyVersion()
    {
        var tenantId = Guid.NewGuid();
        const string conversationId = "conv-retrying";

        var turn2OutboxId = await RunTwoTurnsAsync(
            tenantId,
            conversationId,
            afterTurn1Claimed: async envelope =>
                await _store.MarkFailedAsync(
                    envelope.OutboxId, "HttpRequestException", TimeSpan.FromSeconds(30), CancellationToken.None));

        var claimed = await _store.ClaimBatchAsync(10, TimeSpan.FromSeconds(90), CancellationToken.None);

        Assert.DoesNotContain(claimed, e => e.OutboxId == turn2OutboxId);
    }

    /// <summary>
    /// Regression coverage for the 2026-07-28 incident: OutboxDispatcherService claimed a batch
    /// (status='publishing', locked_until in the future) and then died before ever calling
    /// MarkPublishedAsync/MarkFailedAsync - e.g. a container restart mid-dispatch. The old
    /// ClaimBatchAsync WHERE clause only reclaimed status IN ('pending','failed'), so once
    /// locked_until elapsed the row was orphaned forever and permanently blocked every later
    /// journey_version of the same conversation, with no error ever logged (ClaimBatchAsync just
    /// returned an empty batch, indistinguishable from "nothing to do").
    /// </summary>
    [Fact]
    public async Task OrphanedPublishingPredecessor_WithExpiredLease_IsReclaimedAndUnblocksLaterJourneyVersion()
    {
        var tenantId = Guid.NewGuid();
        const string conversationId = "conv-orphaned-publishing";

        var turn1OutboxId = Guid.Empty;
        var turn2OutboxId = await RunTwoTurnsAsync(
            tenantId,
            conversationId,
            afterTurn1Claimed: async envelope =>
            {
                turn1OutboxId = envelope.OutboxId;

                // Simulate the dispatcher dying mid-flight: leave the row in 'publishing' (never
                // reaches MarkPublishedAsync/MarkFailedAsync) but back-date its lease as if the
                // claim lease from a previous, now-dead process had already expired.
                await using var connection = await _dataSource.OpenConnectionAsync();
                await using var command = new NpgsqlCommand(
                    "UPDATE ops.orchestrator_outbox SET locked_until = now() - interval '1 minute' WHERE outbox_id = @outbox_id",
                    connection);
                command.Parameters.AddWithValue("outbox_id", envelope.OutboxId);
                await command.ExecuteNonQueryAsync();
            });

        var firstReclaim = await _store.ClaimBatchAsync(10, TimeSpan.FromSeconds(90), CancellationToken.None);

        // The orphaned turn-1 row itself must be reclaimable once its lease expired ...
        Assert.Contains(firstReclaim, e => e.OutboxId == turn1OutboxId);
        // ... but turn-2 is still blocked in this same pass: turn-1 is still 'publishing' (not
        // yet resolved) as far as the predecessor check is concerned.
        Assert.DoesNotContain(firstReclaim, e => e.OutboxId == turn2OutboxId);

        await _store.MarkPublishedAsync(turn1OutboxId, CancellationToken.None);

        var secondReclaim = await _store.ClaimBatchAsync(10, TimeSpan.FromSeconds(90), CancellationToken.None);

        Assert.Contains(secondReclaim, e => e.OutboxId == turn2OutboxId);
    }

    /// <summary>
    /// Drives two conversation turns through the real Inbox/Outbox transaction, claims (and thus
    /// locks in 'publishing') the turn-1 effect, lets the caller decide how it resolves, then
    /// completes turn 2 and returns the journey_version=2 outbox id to assert against.
    /// </summary>
    private async Task<Guid> RunTwoTurnsAsync(
        Guid tenantId, string conversationId, Func<OutboxEnvelope, Task> afterTurn1Claimed)
    {
        var turn1 = await _store.TryAcquireAsync(
            tenantId, "msg-1", conversationId, DateTimeOffset.UtcNow, CancellationToken.None);
        Assert.Equal(InboxAcquireResult.Acquired, turn1.Result);

        await _store.CompleteAsync(
            new CompleteMessageCommand(
                tenantId, "msg-1", conversationId, DateTimeOffset.UtcNow, ConversationCheckpoint.StartedState, null,
                turn1.Checkpoint!.Version,
                [new DurableEffect(OutboxEffectTypes.ChannelReply, $"reply:{conversationId}:1", "{}")]),
            CancellationToken.None);

        var turn1Claimed = await _store.ClaimBatchAsync(10, TimeSpan.FromSeconds(90), CancellationToken.None);
        var turn1Envelope = Assert.Single(turn1Claimed, e => e.ConversationId == conversationId);
        await afterTurn1Claimed(turn1Envelope);

        var turn2 = await _store.TryAcquireAsync(
            tenantId, "msg-2", conversationId, DateTimeOffset.UtcNow.AddSeconds(1), CancellationToken.None);
        Assert.Equal(InboxAcquireResult.Acquired, turn2.Result);

        await _store.CompleteAsync(
            new CompleteMessageCommand(
                tenantId, "msg-2", conversationId, DateTimeOffset.UtcNow.AddSeconds(1), ConversationCheckpoint.StartedState, null,
                turn2.Checkpoint!.Version,
                [new DurableEffect(OutboxEffectTypes.ChannelReply, $"reply:{conversationId}:2", "{}")]),
            CancellationToken.None);

        await using var connection = await _dataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand(
            "SELECT outbox_id FROM ops.orchestrator_outbox WHERE tenant_id = @tenant_id AND conversation_id = @conversation_id AND journey_version = 2",
            connection);
        command.Parameters.AddWithValue("tenant_id", tenantId);
        command.Parameters.AddWithValue("conversation_id", conversationId);
        return (Guid)(await command.ExecuteScalarAsync())!;
    }
}
