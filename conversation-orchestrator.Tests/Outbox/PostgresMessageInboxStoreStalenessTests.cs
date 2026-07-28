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
/// Coverage for the orchestrator_outbox_oldest_unresolved_seconds gauge added after the
/// 2026-07-28 incident: a dispatcher that has gone silent (e.g. every candidate blocked by an
/// orphaned predecessor) claims nothing and logs nothing, so the only way to catch it before a
/// customer notices is a staleness signal.
/// </summary>
public sealed class PostgresMessageInboxStoreStalenessTests : IAsyncLifetime
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
    public async Task NoOutboxRows_ReturnsNull()
    {
        var age = await _store.GetOldestUnresolvedEffectAgeAsync(CancellationToken.None);

        Assert.Null(age);
    }

    [Fact]
    public async Task PendingEffect_ReturnsItsAge()
    {
        var tenantId = Guid.NewGuid();
        const string conversationId = "conv-staleness-pending";

        await CreateTurnAsync(tenantId, conversationId, messageId: "msg-1", version: 0);

        var age = await _store.GetOldestUnresolvedEffectAgeAsync(CancellationToken.None);

        Assert.NotNull(age);
        Assert.True(age >= TimeSpan.Zero);
        Assert.True(age < TimeSpan.FromMinutes(1), "a just-created effect should read as a few seconds old, not stale");
    }

    [Fact]
    public async Task OnlyParkedDeadLetter_ReturnsNull()
    {
        var tenantId = Guid.NewGuid();
        const string conversationId = "conv-staleness-parked";

        await CreateTurnAsync(tenantId, conversationId, messageId: "msg-1", version: 0);
        var claimed = await _store.ClaimBatchAsync(10, TimeSpan.FromSeconds(90), CancellationToken.None);
        var envelope = Assert.Single(claimed, e => e.ConversationId == conversationId);

        await _store.MarkFailedAsync(
            envelope.OutboxId, "NonRetryableDispatchException", ParkedRetryDelay, CancellationToken.None);

        var age = await _store.GetOldestUnresolvedEffectAgeAsync(CancellationToken.None);

        Assert.Null(age);
    }

    [Fact]
    public async Task StillRetryingFailure_CountsTowardStaleness()
    {
        var tenantId = Guid.NewGuid();
        const string conversationId = "conv-staleness-retrying";

        await CreateTurnAsync(tenantId, conversationId, messageId: "msg-1", version: 0);
        var claimed = await _store.ClaimBatchAsync(10, TimeSpan.FromSeconds(90), CancellationToken.None);
        var envelope = Assert.Single(claimed, e => e.ConversationId == conversationId);

        await _store.MarkFailedAsync(
            envelope.OutboxId, "HttpRequestException", TimeSpan.FromSeconds(30), CancellationToken.None);

        var age = await _store.GetOldestUnresolvedEffectAgeAsync(CancellationToken.None);

        Assert.NotNull(age);
    }

    private async Task CreateTurnAsync(
        Guid tenantId, string conversationId, string messageId, long version)
    {
        var lease = await _store.TryAcquireAsync(
            tenantId, messageId, conversationId, DateTimeOffset.UtcNow, CancellationToken.None);
        Assert.Equal(InboxAcquireResult.Acquired, lease.Result);

        await _store.CompleteAsync(
            new CompleteMessageCommand(
                tenantId, messageId, conversationId, DateTimeOffset.UtcNow, ConversationCheckpoint.StartedState, null,
                lease.Checkpoint!.Version,
                [new DurableEffect(OutboxEffectTypes.ChannelReply, $"reply:{conversationId}:{messageId}", "{}")]),
            CancellationToken.None);
    }
}
