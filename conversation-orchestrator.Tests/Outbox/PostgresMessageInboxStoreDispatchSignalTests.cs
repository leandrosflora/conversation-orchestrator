using System.Diagnostics;
using Microsoft.Extensions.Options;
using Npgsql;
using Testcontainers.PostgreSql;
using conversation_orchestrator.Adapters.Outbound.Persistence;
using conversation_orchestrator.Application.Outbox;
using conversation_orchestrator.Application.Ports.Outbound;
using conversation_orchestrator.Configuration;
using conversation_orchestrator.Domain;
using Xunit;

namespace conversation_orchestrator.Tests.Outbox;

/// <summary>
/// Regression coverage for the outbox dispatcher's event-driven wakeup: CompleteAsync should signal
/// a waiting dispatcher immediately instead of leaving it to discover new effects only after the
/// idle poll timeout elapses.
/// </summary>
public sealed class PostgresMessageInboxStoreDispatchSignalTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder()
        .WithImage("postgres:16-alpine")
        .WithDatabase("conversational_ai")
        .WithUsername("postgres")
        .WithPassword("postgres")
        .Build();

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
    public async Task CompleteAsync_WithEffects_WakesUpAWaitingCallerLongBeforeTheTimeout()
    {
        var tenantId = Guid.NewGuid();
        const string conversationId = "conv-signal-wakeup";

        var turn = await _store.TryAcquireAsync(
            tenantId, "msg-1", conversationId, DateTimeOffset.UtcNow, CancellationToken.None);
        Assert.Equal(InboxAcquireResult.Acquired, turn.Result);

        var waitTask = _store.WaitForPendingEffectAsync(TimeSpan.FromSeconds(30), CancellationToken.None);
        var stopwatch = Stopwatch.StartNew();

        await _store.CompleteAsync(
            new CompleteMessageCommand(
                tenantId, "msg-1", conversationId, DateTimeOffset.UtcNow, JourneyStage.Started, null,
                turn.Checkpoint!.Version,
                [new DurableEffect(OutboxEffectTypes.ChannelReply, $"reply:{conversationId}:1", "{}")]),
            CancellationToken.None);

        var signaled = await waitTask.WaitAsync(TimeSpan.FromSeconds(5));
        stopwatch.Stop();

        Assert.True(signaled, "CompleteAsync should wake the waiting dispatcher, not leave it to time out");
        Assert.True(
            stopwatch.Elapsed < TimeSpan.FromSeconds(5),
            $"expected an immediate wakeup, took {stopwatch.Elapsed}");
    }

    [Fact]
    public async Task WaitForPendingEffectAsync_NoSignal_TimesOutAndReturnsFalse()
    {
        var stopwatch = Stopwatch.StartNew();

        var signaled = await _store.WaitForPendingEffectAsync(TimeSpan.FromMilliseconds(200), CancellationToken.None);

        stopwatch.Stop();
        Assert.False(signaled);
        Assert.True(stopwatch.Elapsed >= TimeSpan.FromMilliseconds(150), $"timed out too early: {stopwatch.Elapsed}");
    }

    [Fact]
    public async Task CompleteAsync_CalledTwiceBeforeAnyoneWaits_CoalescesIntoASingleSignal()
    {
        var tenantId = Guid.NewGuid();
        const string conversationId = "conv-signal-coalesce";

        var turn1 = await _store.TryAcquireAsync(
            tenantId, "msg-1", conversationId, DateTimeOffset.UtcNow, CancellationToken.None);
        await _store.CompleteAsync(
            new CompleteMessageCommand(
                tenantId, "msg-1", conversationId, DateTimeOffset.UtcNow, JourneyStage.Started, null,
                turn1.Checkpoint!.Version,
                [new DurableEffect(OutboxEffectTypes.ChannelReply, $"reply:{conversationId}:1", "{}")]),
            CancellationToken.None);

        var turn2 = await _store.TryAcquireAsync(
            tenantId, "msg-2", conversationId, DateTimeOffset.UtcNow.AddSeconds(1), CancellationToken.None);
        await _store.CompleteAsync(
            new CompleteMessageCommand(
                tenantId, "msg-2", conversationId, DateTimeOffset.UtcNow.AddSeconds(1), JourneyStage.Started, null,
                turn2.Checkpoint!.Version,
                [new DurableEffect(OutboxEffectTypes.ChannelReply, $"reply:{conversationId}:2", "{}")]),
            CancellationToken.None);

        var firstWait = await _store.WaitForPendingEffectAsync(TimeSpan.FromSeconds(5), CancellationToken.None);
        var secondWait = await _store.WaitForPendingEffectAsync(TimeSpan.FromMilliseconds(200), CancellationToken.None);

        Assert.True(firstWait, "the first wait should consume the coalesced signal");
        Assert.False(secondWait, "two completions before anyone waited should coalesce into one signal, not two");
    }
}
