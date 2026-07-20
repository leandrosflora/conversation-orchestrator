using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Moq;
using conversation_orchestrator.Application.Outbox;
using conversation_orchestrator.Application.Ports.Outbound;
using conversation_orchestrator.Domain;
using conversation_orchestrator.Tests.Testing;
using Xunit;

namespace conversation_orchestrator.Tests.Messages;

public class MessageIngestionEndpointsTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _baseFactory;

    public MessageIngestionEndpointsTests(WebApplicationFactory<Program> factory)
    {
        _baseFactory = factory;
    }

    [Fact]
    public async Task PostMessages_ValidTextMessage_ReturnsAccepted()
    {
        var agentRuntime = new Mock<IAgentRuntimeClient>();
        agentRuntime
            .Setup(a => a.ProcessAsync(It.IsAny<AgentRuntimeRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AgentRuntimeResult { Intent = "faq", ReplyText = "Oi!", RequiresHandoff = false });
        var client = CreateClient(agentRuntime.Object);

        var response = await client.PostAsJsonAsync("/messages", new
        {
            MessageId = "wamid.1",
            From = "5511999990000",
            ConversationId = "5511999990000",
            Type = 0,
            Text = "Ola",
            ReceivedAt = DateTimeOffset.UtcNow
        });

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        agentRuntime.Verify(a => a.ProcessAsync(It.IsAny<AgentRuntimeRequest>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task PostMessages_ValidInteractiveMessage_ReturnsAccepted()
    {
        var agentRuntime = new Mock<IAgentRuntimeClient>();
        agentRuntime
            .Setup(a => a.ProcessAsync(It.IsAny<AgentRuntimeRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AgentRuntimeResult { Intent = "accept_offer", ReplyText = "Combinado!", RequiresHandoff = false });
        var client = CreateClient(agentRuntime.Object);

        var response = await client.PostAsJsonAsync("/messages", new
        {
            MessageId = "wamid.2",
            From = "5511999990000",
            ConversationId = "5511999990000",
            Type = 1,
            Interactive = new { Id = "opt-accept", Title = "Aceitar" },
            ReceivedAt = DateTimeOffset.UtcNow
        });

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
    }

    [Fact]
    public async Task PostMessages_MissingRequiredFields_ReturnsBadRequestWithoutCallingAgentRuntime()
    {
        var agentRuntime = new Mock<IAgentRuntimeClient>();
        var client = CreateClient(agentRuntime.Object);

        var response = await client.PostAsJsonAsync("/messages", new
        {
            Type = 0,
            Text = "Ola",
            ReceivedAt = DateTimeOffset.UtcNow
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        agentRuntime.Verify(a => a.ProcessAsync(It.IsAny<AgentRuntimeRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task PostMessages_AgentRuntimeUnavailable_StillReturnsAcceptedAndRequestsHandoff()
    {
        var agentRuntime = new Mock<IAgentRuntimeClient>();
        agentRuntime
            .Setup(a => a.ProcessAsync(It.IsAny<AgentRuntimeRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(AgentRuntimeResult.Unavailable());
        var handoff = new Mock<IHandoffServiceClient>();
        var reply = new Mock<IChannelReplyClient>();
        var audit = new Mock<IAuditServiceClient>();
        var client = CreateClient(
            agentRuntime.Object, handoffClient: handoff.Object, replyClient: reply.Object, auditClient: audit.Object);

        var response = await client.PostAsJsonAsync("/messages", new
        {
            MessageId = "wamid.3",
            From = "5511999990000",
            ConversationId = "5511999990000",
            Type = 0,
            Text = "Ola",
            ReceivedAt = DateTimeOffset.UtcNow
        });

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        handoff.Verify(
            h => h.RequestHandoffAsync(
                It.Is<HandoffRequest>(r => r.Reason == AgentRuntimeResult.AgentRuntimeUnavailableReason),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()),
            Times.Once);
        reply.Verify(r => r.SendReplyAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        audit.Verify(
            a => a.RecordJourneyEventAsync(It.IsAny<JourneyAuditEvent>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task PostMessages_AgentRuntimeRecommendsHandoff_RequestsHandoffAndRecordsAudit()
    {
        var agentRuntime = new Mock<IAgentRuntimeClient>();
        agentRuntime
            .Setup(a => a.ProcessAsync(It.IsAny<AgentRuntimeRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AgentRuntimeResult
            {
                Intent = "complaint",
                RequiresHandoff = true,
                HandoffReason = "low_confidence"
            });
        var handoff = new Mock<IHandoffServiceClient>();
        var audit = new Mock<IAuditServiceClient>();
        var reply = new Mock<IChannelReplyClient>();
        var client = CreateClient(
            agentRuntime.Object, handoffClient: handoff.Object, auditClient: audit.Object, replyClient: reply.Object);

        var response = await client.PostAsJsonAsync("/messages", new
        {
            MessageId = "wamid.5",
            From = "5511999990000",
            ConversationId = "5511999990000",
            Type = 0,
            Text = "Isso e um absurdo",
            ReceivedAt = DateTimeOffset.UtcNow
        });

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        handoff.Verify(
            h => h.RequestHandoffAsync(
                It.Is<HandoffRequest>(r => r.Reason == "low_confidence"), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Once);
        reply.Verify(r => r.SendReplyAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        audit.Verify(
            a => a.RecordJourneyEventAsync(
                It.Is<JourneyAuditEvent>(e => e.Outcome == "handoff"), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task PostMessages_ReplyDelivered_AuditRecordedRegardlessOfOutcome()
    {
        var agentRuntime = new Mock<IAgentRuntimeClient>();
        agentRuntime
            .Setup(a => a.ProcessAsync(It.IsAny<AgentRuntimeRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AgentRuntimeResult { Intent = "faq", ReplyText = "Oi!", RequiresHandoff = false });
        var reply = new Mock<IChannelReplyClient>();
        var audit = new Mock<IAuditServiceClient>();
        var eventPublisher = new Mock<IConversationEventPublisher>();
        var client = CreateClient(agentRuntime.Object, replyClient: reply.Object, auditClient: audit.Object, eventPublisher: eventPublisher.Object);

        var response = await client.PostAsJsonAsync("/messages", new
        {
            MessageId = "wamid.4",
            From = "5511999990000",
            ConversationId = "5511999990000",
            Type = 0,
            Text = "Ola",
            ReceivedAt = DateTimeOffset.UtcNow
        });

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        reply.Verify(r => r.SendReplyAsync("5511999990000", "Oi!", It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
        audit.Verify(
            a => a.RecordJourneyEventAsync(It.IsAny<JourneyAuditEvent>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Once);
        eventPublisher.Verify(
            e => e.PublishIntentDetectedAsync(It.IsAny<IntentDetectedEvent>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task PostMessages_SameMessageIdTwice_OnlyProcessesOnce()
    {
        var agentRuntime = new Mock<IAgentRuntimeClient>();
        agentRuntime
            .Setup(a => a.ProcessAsync(It.IsAny<AgentRuntimeRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AgentRuntimeResult { Intent = "faq", ReplyText = "Oi!", RequiresHandoff = false });
        var client = CreateClient(agentRuntime.Object);
        var payload = new
        {
            MessageId = "wamid.duplicate-1",
            From = "5511999990000",
            ConversationId = "5511999990000",
            Type = 0,
            Text = "Ola",
            ReceivedAt = DateTimeOffset.UtcNow
        };

        var first = await client.PostAsJsonAsync("/messages", payload);
        var second = await client.PostAsJsonAsync("/messages", payload);

        Assert.Equal(HttpStatusCode.Accepted, first.StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, second.StatusCode);
        agentRuntime.Verify(a => a.ProcessAsync(It.IsAny<AgentRuntimeRequest>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task PostMessages_IntentClassifiesToLegalTrigger_SavesExpectedNextJourneyStage()
    {
        var agentRuntime = new Mock<IAgentRuntimeClient>();
        agentRuntime
            .Setup(a => a.ProcessAsync(It.IsAny<AgentRuntimeRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AgentRuntimeResult { Intent = "selecionar_contrato", ReplyText = "Ok!", RequiresHandoff = false });
        ConversationSession? saved = null;
        var memoryClient = new Mock<IConversationMemoryClient>();
        memoryClient
            .Setup(c => c.GetOrCreateSessionAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string conversationId, CancellationToken _) => new ConversationSession
            {
                ConversationId = conversationId,
                CreatedAt = DateTimeOffset.UtcNow,
                LastMessageAt = DateTimeOffset.UtcNow,
                JourneyStage = JourneyStage.CustomerIdentified
            });
        memoryClient
            .Setup(c => c.SaveSessionAsync(It.IsAny<ConversationSession>(), It.IsAny<CancellationToken>()))
            .Callback<ConversationSession, CancellationToken>((session, _) => saved = session)
            .Returns(Task.CompletedTask);
        var client = CreateClient(
            agentRuntime.Object,
            conversationMemoryClient: memoryClient.Object,
            seedConversationId: "5511999990000",
            seedJourneyStage: JourneyStage.CustomerIdentified);

        var response = await client.PostAsJsonAsync("/messages", new
        {
            MessageId = "wamid.stage-1",
            From = "5511999990000",
            ConversationId = "5511999990000",
            Type = 0,
            Text = "Quero o contrato 123",
            ReceivedAt = DateTimeOffset.UtcNow
        });

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.NotNull(saved);
        Assert.Equal(JourneyStage.ContractSelected, saved!.JourneyStage);
    }

    [Fact]
    public async Task PostMessages_UnrecognizedIntent_LeavesJourneyStageUnchanged()
    {
        var agentRuntime = new Mock<IAgentRuntimeClient>();
        agentRuntime
            .Setup(a => a.ProcessAsync(It.IsAny<AgentRuntimeRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AgentRuntimeResult { Intent = "faq", ReplyText = "Oi!", RequiresHandoff = false });
        ConversationSession? saved = null;
        var memoryClient = new Mock<IConversationMemoryClient>();
        memoryClient
            .Setup(c => c.GetOrCreateSessionAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string conversationId, CancellationToken _) => new ConversationSession
            {
                ConversationId = conversationId,
                CreatedAt = DateTimeOffset.UtcNow,
                LastMessageAt = DateTimeOffset.UtcNow,
                JourneyStage = JourneyStage.CustomerIdentified
            });
        memoryClient
            .Setup(c => c.SaveSessionAsync(It.IsAny<ConversationSession>(), It.IsAny<CancellationToken>()))
            .Callback<ConversationSession, CancellationToken>((session, _) => saved = session)
            .Returns(Task.CompletedTask);
        var client = CreateClient(
            agentRuntime.Object,
            conversationMemoryClient: memoryClient.Object,
            seedConversationId: "5511999990000",
            seedJourneyStage: JourneyStage.CustomerIdentified);

        var response = await client.PostAsJsonAsync("/messages", new
        {
            MessageId = "wamid.stage-2",
            From = "5511999990000",
            ConversationId = "5511999990000",
            Type = 0,
            Text = "Oi, tudo bem?",
            ReceivedAt = DateTimeOffset.UtcNow
        });

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.NotNull(saved);
        Assert.Equal(JourneyStage.CustomerIdentified, saved!.JourneyStage);
    }

    [Fact]
    public async Task PostMessages_RequiresHandoff_OverridesOtherwiseLegalTransition()
    {
        var agentRuntime = new Mock<IAgentRuntimeClient>();
        agentRuntime
            .Setup(a => a.ProcessAsync(It.IsAny<AgentRuntimeRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AgentRuntimeResult
            {
                Intent = "selecionar_contrato",
                RequiresHandoff = true,
                HandoffReason = "low_confidence"
            });
        ConversationSession? saved = null;
        var memoryClient = new Mock<IConversationMemoryClient>();
        memoryClient
            .Setup(c => c.GetOrCreateSessionAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string conversationId, CancellationToken _) => new ConversationSession
            {
                ConversationId = conversationId,
                CreatedAt = DateTimeOffset.UtcNow,
                LastMessageAt = DateTimeOffset.UtcNow,
                JourneyStage = JourneyStage.CustomerIdentified
            });
        memoryClient
            .Setup(c => c.SaveSessionAsync(It.IsAny<ConversationSession>(), It.IsAny<CancellationToken>()))
            .Callback<ConversationSession, CancellationToken>((session, _) => saved = session)
            .Returns(Task.CompletedTask);
        var handoff = new Mock<IHandoffServiceClient>();
        var audit = new Mock<IAuditServiceClient>();
        var client = CreateClient(
            agentRuntime.Object,
            handoffClient: handoff.Object,
            auditClient: audit.Object,
            conversationMemoryClient: memoryClient.Object,
            seedConversationId: "5511999990000",
            seedJourneyStage: JourneyStage.CustomerIdentified);

        var response = await client.PostAsJsonAsync("/messages", new
        {
            MessageId = "wamid.stage-3",
            From = "5511999990000",
            ConversationId = "5511999990000",
            Type = 0,
            Text = "Quero o contrato 123",
            ReceivedAt = DateTimeOffset.UtcNow
        });

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.NotNull(saved);
        Assert.Equal(JourneyStage.HandoffRequested, saved!.JourneyStage);
    }

    private HttpClient CreateClient(
        IAgentRuntimeClient agentRuntimeClient,
        IChannelReplyClient? replyClient = null,
        IConversationEventPublisher? eventPublisher = null,
        IHandoffServiceClient? handoffClient = null,
        IAuditServiceClient? auditClient = null,
        IConversationMemoryClient? conversationMemoryClient = null,
        IMessageInboxStore? inboxStore = null,
        string? seedConversationId = null,
        JourneyStage? seedJourneyStage = null)
    {
        var reply = replyClient ?? Mock.Of<IChannelReplyClient>();
        var events = eventPublisher ?? Mock.Of<IConversationEventPublisher>();
        var handoff = handoffClient ?? Mock.Of<IHandoffServiceClient>();
        var audit = auditClient ?? Mock.Of<IAuditServiceClient>();
        var memory = conversationMemoryClient ?? CreateDefaultConversationMemoryClient();

        var factory = _baseFactory.WithWebHostBuilder(builder =>
        {
            TestAuth.ConfigureSigningKey(builder);
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IAgentRuntimeClient>();
                services.AddSingleton(agentRuntimeClient);

                services.RemoveAll<IChannelReplyClient>();
                services.AddSingleton(reply);

                services.RemoveAll<IConversationEventPublisher>();
                services.AddSingleton(events);

                services.RemoveAll<IHandoffServiceClient>();
                services.AddSingleton(handoff);

                services.RemoveAll<IAuditServiceClient>();
                services.AddSingleton(audit);

                services.RemoveAll<IConversationMemoryClient>();
                services.AddSingleton(memory);

                services.RemoveAll<IMessageInboxStore>();
                services.AddSingleton(inboxStore ?? new InMemoryMessageInboxStore(
                    reply, events, handoff, audit, memory, seedConversationId, seedJourneyStage));
            });
        });

        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", TestAuth.IssueToken());
        client.DefaultRequestHeaders.Add("X-Tenant-Id", TestAuth.TenantId);
        return client;
    }

    /// <summary>Minimal in-memory stand-in for PostgresMessageInboxStore + OutboxDispatcherService,
    /// tracking real per-messageId state and a per-conversation checkpoint so tests like
    /// duplicate-message handling behave the same way they would against the real store, without
    /// needing a live PostgreSQL. CompleteAsync also dispatches the durable effects synchronously
    /// to the same client interfaces OutboxDispatcherService.DispatchPayloadAsync would call
    /// asynchronously in production, so endpoint tests can assert on them without racing a
    /// background poller. Matches the outbox-based IMessageInboxStore contract introduced
    /// alongside OutboxDispatcherService - see conversation-orchestrator's
    /// Application/UseCases/IngestMessageUseCase.cs for how it's consumed.</summary>
    private sealed class InMemoryMessageInboxStore(
        IChannelReplyClient replyClient,
        IConversationEventPublisher eventPublisher,
        IHandoffServiceClient handoffClient,
        IAuditServiceClient auditClient,
        IConversationMemoryClient memoryClient,
        string? seedConversationId = null,
        JourneyStage? seedJourneyStage = null) : IMessageInboxStore
    {
        private readonly HashSet<string> _completed = new();
        private readonly HashSet<string> _inProgress = new();
        private readonly Dictionary<string, ConversationCheckpoint> _checkpoints =
            seedConversationId is not null && seedJourneyStage is not null
                ? new() { [seedConversationId] = new ConversationCheckpoint(seedJourneyStage.Value, null, 0, null, null) }
                : new();

        public Task<InboxLease> TryAcquireAsync(
            Guid tenantId,
            string messageId,
            string conversationId,
            DateTimeOffset receivedAt,
            CancellationToken cancellationToken)
        {
            if (_completed.Contains(messageId))
            {
                return Task.FromResult(new InboxLease(InboxAcquireResult.Completed));
            }

            if (!_inProgress.Add(messageId))
            {
                return Task.FromResult(new InboxLease(InboxAcquireResult.InProgress));
            }

            var checkpoint = _checkpoints.TryGetValue(conversationId, out var existing)
                ? existing
                : new ConversationCheckpoint(JourneyStage.Started, null, 0, null, null);
            return Task.FromResult(new InboxLease(InboxAcquireResult.Acquired, checkpoint));
        }

        public async Task CompleteAsync(CompleteMessageCommand command, CancellationToken cancellationToken)
        {
            _inProgress.Remove(command.MessageId);
            _completed.Add(command.MessageId);
            _checkpoints[command.ConversationId] = new ConversationCheckpoint(
                command.JourneyStage,
                command.LastIntent,
                command.ExpectedVersion + 1,
                command.ReceivedAt,
                command.MessageId);

            foreach (var effect in command.Effects)
            {
                await DispatchEffectAsync(effect, cancellationToken);
            }
        }

        public Task MarkFailedAsync(
            Guid tenantId, string messageId, string errorType, CancellationToken cancellationToken)
        {
            _inProgress.Remove(messageId);
            return Task.CompletedTask;
        }

        private async Task DispatchEffectAsync(DurableEffect effect, CancellationToken cancellationToken)
        {
            switch (effect.EffectType)
            {
                case OutboxEffectTypes.MemoryAppendMessage:
                {
                    var payload = Deserialize<MemoryAppendMessageEffect>(effect.Payload);
                    await memoryClient.AppendMessageAsync(
                        payload.ConversationId,
                        payload.Role,
                        payload.Text,
                        payload.ExternalMessageId ?? effect.IdempotencyKey,
                        cancellationToken);
                    return;
                }
                case OutboxEffectTypes.MemorySaveSession:
                {
                    var payload = Deserialize<MemorySaveSessionEffect>(effect.Payload);
                    var stage = Enum.TryParse<JourneyStage>(payload.JourneyStage, true, out var parsed)
                        ? parsed
                        : JourneyStage.Started;
                    await memoryClient.SaveSessionAsync(
                        new ConversationSession
                        {
                            ConversationId = payload.ConversationId,
                            CreatedAt = payload.CreatedAt,
                            LastMessageAt = payload.LastMessageAt,
                            JourneyStage = stage,
                            LastIntent = payload.LastIntent
                        },
                        cancellationToken);
                    return;
                }
                case OutboxEffectTypes.ChannelReply:
                {
                    var payload = Deserialize<ChannelReplyEffect>(effect.Payload);
                    await replyClient.SendReplyAsync(
                        payload.ConversationId, payload.ReplyText, effect.IdempotencyKey, cancellationToken);
                    return;
                }
                case OutboxEffectTypes.HandoffRequest:
                {
                    var payload = Deserialize<HandoffRequestEffect>(effect.Payload);
                    await handoffClient.RequestHandoffAsync(
                        new HandoffRequest { ConversationId = payload.ConversationId, Reason = payload.Reason },
                        effect.IdempotencyKey,
                        cancellationToken);
                    return;
                }
                case OutboxEffectTypes.AuditRecord:
                {
                    var payload = Deserialize<AuditRecordEffect>(effect.Payload);
                    await auditClient.RecordJourneyEventAsync(
                        new JourneyAuditEvent
                        {
                            ConversationId = payload.ConversationId,
                            Intent = payload.Intent,
                            Outcome = payload.Outcome,
                            Timestamp = payload.Timestamp
                        },
                        effect.IdempotencyKey,
                        cancellationToken);
                    return;
                }
                case OutboxEffectTypes.IntentDetected:
                {
                    var payload = Deserialize<IntentDetectedEffect>(effect.Payload);
                    await eventPublisher.PublishIntentDetectedAsync(
                        new IntentDetectedEvent
                        {
                            ConversationId = payload.ConversationId,
                            Intent = payload.Intent,
                            Confidence = payload.Confidence,
                            DetectedAt = payload.DetectedAt
                        },
                        cancellationToken);
                    return;
                }
                case OutboxEffectTypes.StateChanged:
                {
                    var payload = Deserialize<StateChangedEffect>(effect.Payload);
                    await eventPublisher.PublishConversationStateChangedAsync(
                        new ConversationStateChangedEvent
                        {
                            ConversationId = payload.ConversationId,
                            PreviousStage = payload.PreviousStage,
                            NewStage = payload.NewStage,
                            ChangedAt = payload.ChangedAt
                        },
                        cancellationToken);
                    return;
                }
            }
        }

        private static T Deserialize<T>(string payload) =>
            JsonSerializer.Deserialize<T>(payload)
            ?? throw new InvalidOperationException($"Outbox payload for {typeof(T).Name} is invalid.");
    }

    private static IConversationMemoryClient CreateDefaultConversationMemoryClient()
    {
        var client = new Mock<IConversationMemoryClient>();
        client
            .Setup(c => c.GetOrCreateSessionAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string conversationId, CancellationToken _) => new ConversationSession
            {
                ConversationId = conversationId,
                CreatedAt = DateTimeOffset.UtcNow,
                LastMessageAt = DateTimeOffset.UtcNow
            });
        return client.Object;
    }
}
