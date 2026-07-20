using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using conversation_orchestrator.Adapters.Outbound.Persistence;
using conversation_orchestrator.Application.Outbox;
using conversation_orchestrator.Application.Ports.Outbound;
using conversation_orchestrator.Platform;
using Xunit;

namespace conversation_orchestrator.Tests.Outbox;

public class OutboxDispatcherServiceTests
{
    [Fact]
    public async Task NonRetryableFailure_IsParkedInsteadOfRescheduledSoon()
    {
        var effect = DurableEffectFactory.Create(
            OutboxEffectTypes.ChannelReply, "reply:tenant:msg-1", new ChannelReplyEffect("5511999990000", "Oi!"));
        var envelope = new OutboxEnvelope(
            Guid.NewGuid(), Guid.Parse("00000000-0000-0000-0000-000000000001"), "msg-1", "5511999990000",
            effect.EffectType, effect.IdempotencyKey, effect.Payload, AttemptCount: 1);
        var store = new FakeOutboxStore([envelope]);
        var replyClient = new ThrowingChannelReplyClient(new NonRetryableDispatchException("permanently rejected"));

        await RunOneDispatchCycleAsync(store, replyClient);

        var (_, _, retryDelay) = Assert.Single(store.FailedCalls);
        Assert.True(retryDelay > TimeSpan.FromDays(300), "non-retryable failures must be parked far in the future, not retried soon");
    }

    [Fact]
    public async Task FailureExceedingMaxAttempts_IsParkedEvenWithoutExplicitNonRetryableSignal()
    {
        var effect = DurableEffectFactory.Create(
            OutboxEffectTypes.ChannelReply, "reply:tenant:msg-2", new ChannelReplyEffect("5511999990000", "Oi!"));
        var envelope = new OutboxEnvelope(
            Guid.NewGuid(), Guid.Parse("00000000-0000-0000-0000-000000000001"), "msg-2", "5511999990000",
            effect.EffectType, effect.IdempotencyKey, effect.Payload, AttemptCount: 20);
        var store = new FakeOutboxStore([envelope]);
        var replyClient = new ThrowingChannelReplyClient(new HttpRequestException("still unreachable"));

        await RunOneDispatchCycleAsync(store, replyClient);

        var (_, _, retryDelay) = Assert.Single(store.FailedCalls);
        Assert.True(retryDelay > TimeSpan.FromDays(300), "an effect stuck for MaxAttemptsBeforeParking attempts must be parked, not retried forever");
    }

    [Fact]
    public async Task TransientFailure_UnderAttemptCap_UsesShortExponentialBackoff()
    {
        var effect = DurableEffectFactory.Create(
            OutboxEffectTypes.ChannelReply, "reply:tenant:msg-3", new ChannelReplyEffect("5511999990000", "Oi!"));
        var envelope = new OutboxEnvelope(
            Guid.NewGuid(), Guid.Parse("00000000-0000-0000-0000-000000000001"), "msg-3", "5511999990000",
            effect.EffectType, effect.IdempotencyKey, effect.Payload, AttemptCount: 1);
        var store = new FakeOutboxStore([envelope]);
        var replyClient = new ThrowingChannelReplyClient(new HttpRequestException("connection refused"));

        await RunOneDispatchCycleAsync(store, replyClient);

        var (_, _, retryDelay) = Assert.Single(store.FailedCalls);
        Assert.True(retryDelay < TimeSpan.FromMinutes(10), "early transient failures should keep the short exponential backoff");
    }

    private static async Task RunOneDispatchCycleAsync(FakeOutboxStore store, IChannelReplyClient replyClient)
    {
        var services = new ServiceCollection();
        services.AddScoped<TenantContext>();
        services.AddSingleton(replyClient);
        var provider = services.BuildServiceProvider();

        var dispatcher = new OutboxDispatcherService(
            store,
            provider.GetRequiredService<IServiceScopeFactory>(),
            new PlatformMetrics(),
            NullLogger<OutboxDispatcherService>.Instance);

        using var cts = new CancellationTokenSource();
        var run = dispatcher.StartAsync(cts.Token);
        await store.DrainedOnce.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cts.Cancel();
        try
        {
            await run;
        }
        catch (OperationCanceledException)
        {
        }
    }

    private sealed class ThrowingChannelReplyClient(Exception exception) : IChannelReplyClient
    {
        public Task SendReplyAsync(
            string conversationId, string replyText, string idempotencyKey, CancellationToken cancellationToken) =>
            throw exception;
    }

    private sealed class FakeOutboxStore(List<OutboxEnvelope> pending) : IOutboxStore
    {
        public List<(Guid OutboxId, string ErrorType, TimeSpan RetryDelay)> FailedCalls { get; } = [];
        public TaskCompletionSource DrainedOnce { get; } = new();
        private bool _served;

        public Task<IReadOnlyList<OutboxEnvelope>> ClaimBatchAsync(
            int batchSize, TimeSpan lease, CancellationToken cancellationToken)
        {
            if (_served)
            {
                return Task.FromResult<IReadOnlyList<OutboxEnvelope>>([]);
            }
            _served = true;
            return Task.FromResult<IReadOnlyList<OutboxEnvelope>>(pending);
        }

        public Task MarkPublishedAsync(Guid outboxId, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task MarkFailedAsync(
            Guid outboxId, string errorType, TimeSpan retryDelay, CancellationToken cancellationToken)
        {
            FailedCalls.Add((outboxId, errorType, retryDelay));
            DrainedOnce.TrySetResult();
            return Task.CompletedTask;
        }

        public async Task<bool> WaitForPendingEffectAsync(TimeSpan timeout, CancellationToken cancellationToken)
        {
            await Task.Delay(timeout, cancellationToken);
            return false;
        }
    }
}
