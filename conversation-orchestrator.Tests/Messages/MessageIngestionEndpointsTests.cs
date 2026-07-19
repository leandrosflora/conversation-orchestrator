using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Moq;
using conversation_orchestrator.Application.Ports.Outbound;
using conversation_orchestrator.Domain;
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
        reply.Verify(r => r.SendReplyAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
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
        reply.Verify(r => r.SendReplyAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
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
        reply.Verify(r => r.SendReplyAsync("5511999990000", "Oi!", It.IsAny<CancellationToken>()), Times.Once);
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
        var client = CreateClient(agentRuntime.Object, conversationMemoryClient: memoryClient.Object);

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
        var client = CreateClient(agentRuntime.Object, conversationMemoryClient: memoryClient.Object);

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
            conversationMemoryClient: memoryClient.Object);

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
        IMessageInboxStore? inboxStore = null)
    {
        var factory = _baseFactory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("InternalAuth:Enabled", "false");
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IAgentRuntimeClient>();
                services.AddSingleton(agentRuntimeClient);

                services.RemoveAll<IChannelReplyClient>();
                services.AddSingleton(replyClient ?? Mock.Of<IChannelReplyClient>());

                services.RemoveAll<IConversationEventPublisher>();
                services.AddSingleton(eventPublisher ?? Mock.Of<IConversationEventPublisher>());

                services.RemoveAll<IHandoffServiceClient>();
                services.AddSingleton(handoffClient ?? Mock.Of<IHandoffServiceClient>());

                services.RemoveAll<IAuditServiceClient>();
                services.AddSingleton(auditClient ?? Mock.Of<IAuditServiceClient>());

                services.RemoveAll<IConversationMemoryClient>();
                services.AddSingleton(conversationMemoryClient ?? CreateDefaultConversationMemoryClient());

                services.RemoveAll<IMessageInboxStore>();
                services.AddSingleton(inboxStore ?? CreateDefaultMessageInboxStore());
            });
        });

        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Tenant-Id", "00000000-0000-0000-0000-000000000001");
        return client;
    }

    private static IMessageInboxStore CreateDefaultMessageInboxStore() => new InMemoryMessageInboxStore();

    /// <summary>Minimal in-memory stand-in for PostgresMessageInboxStore, tracking real
    /// per-messageId state so tests like duplicate-message handling behave the same way
    /// they would against the real store, without needing a live PostgreSQL.</summary>
    private sealed class InMemoryMessageInboxStore : IMessageInboxStore
    {
        private readonly HashSet<string> _completed = new();
        private readonly HashSet<string> _inProgress = new();

        public Task<InboxAcquireResult> TryAcquireAsync(
            string messageId, string conversationId, CancellationToken cancellationToken)
        {
            if (_completed.Contains(messageId))
            {
                return Task.FromResult(InboxAcquireResult.Completed);
            }

            if (!_inProgress.Add(messageId))
            {
                return Task.FromResult(InboxAcquireResult.InProgress);
            }

            return Task.FromResult(InboxAcquireResult.Acquired);
        }

        public Task MarkCompletedAsync(string messageId, CancellationToken cancellationToken)
        {
            _inProgress.Remove(messageId);
            _completed.Add(messageId);
            return Task.CompletedTask;
        }

        public Task MarkFailedAsync(string messageId, string errorType, CancellationToken cancellationToken)
        {
            _inProgress.Remove(messageId);
            return Task.CompletedTask;
        }
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
