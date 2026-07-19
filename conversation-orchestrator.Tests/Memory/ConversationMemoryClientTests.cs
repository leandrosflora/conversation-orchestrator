using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using conversation_orchestrator.Tests.Testing;
using Xunit;
using conversation_orchestrator.Application.Ports.Outbound;
using conversation_orchestrator.Adapters.Outbound.Http;
using conversation_orchestrator.Configuration;
using conversation_orchestrator.Domain;
using conversation_orchestrator.Platform;

namespace conversation_orchestrator.Tests.Memory;

public class ConversationMemoryClientTests
{
    [Fact]
    public async Task GetOrCreateSessionAsync_ExistingSession_ReturnsItsState()
    {
        var handler = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonContent("""
                {"conversation_id": "conv-1", "data": {"createdAt": "2026-01-01T00:00:00Z", "journeyStage": "EligibilityChecked", "lastIntent": "renegotiation_request"}, "updated_at": "2026-01-01T00:00:00Z"}
                """)
        });
        var client = BuildClient(handler);

        var session = await client.GetOrCreateSessionAsync("conv-1", CancellationToken.None);

        Assert.Equal("conv-1", session.ConversationId);
        Assert.Equal(JourneyStage.EligibilityChecked, session.JourneyStage);
        Assert.Equal("renegotiation_request", session.LastIntent);
    }

    [Fact]
    public async Task GetOrCreateSessionAsync_UnparseableJourneyStage_FallsBackToStarted()
    {
        var handler = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonContent("""
                {"conversation_id": "conv-1b", "data": {"createdAt": "2026-01-01T00:00:00Z", "journeyStage": "eligibility", "lastIntent": "renegotiation_request"}, "updated_at": "2026-01-01T00:00:00Z"}
                """)
        });
        var client = BuildClient(handler);

        var session = await client.GetOrCreateSessionAsync("conv-1b", CancellationToken.None);

        Assert.Equal("conv-1b", session.ConversationId);
        Assert.Equal(JourneyStage.Started, session.JourneyStage);
    }

    [Fact]
    public async Task GetOrCreateSessionAsync_NotFound_ReturnsFreshSession()
    {
        var handler = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound));
        var client = BuildClient(handler);

        var session = await client.GetOrCreateSessionAsync("conv-2", CancellationToken.None);

        Assert.Equal("conv-2", session.ConversationId);
        Assert.Equal(JourneyStage.Started, session.JourneyStage);
        Assert.Null(session.LastIntent);
    }

    [Fact]
    public async Task GetOrCreateSessionAsync_ServiceUnreachable_ReturnsFreshSessionWithoutThrowing()
    {
        var handler = new StubHttpMessageHandler(_ => throw new HttpRequestException("connection refused"));
        var client = BuildClient(handler);

        var session = await client.GetOrCreateSessionAsync("conv-3", CancellationToken.None);

        Assert.Equal("conv-3", session.ConversationId);
        Assert.Equal(JourneyStage.Started, session.JourneyStage);
    }

    [Fact]
    public async Task SaveSessionAsync_Success_CallsConversationMemoryServiceOnce()
    {
        HttpRequestMessage? captured = null;
        var handler = new StubHttpMessageHandler(request =>
        {
            captured = request;
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        var client = BuildClient(handler);

        await client.SaveSessionAsync(
            new ConversationSession
            {
                ConversationId = "conv-4",
                CreatedAt = DateTimeOffset.UtcNow,
                LastMessageAt = DateTimeOffset.UtcNow,
                JourneyStage = JourneyStage.AgreementProcessing,
                LastIntent = "faq"
            },
            CancellationToken.None);

        Assert.Equal(1, handler.CallCount);
        Assert.NotNull(captured);
        Assert.Equal(HttpMethod.Put, captured!.Method);
        Assert.EndsWith("/sessions/conv-4", captured.RequestUri!.ToString());
        var body = await captured.Content!.ReadAsStringAsync();
        Assert.Contains("\"data\"", body);
        Assert.Contains("\"journeyStage\":\"AgreementProcessing\"", body);
    }

    [Fact]
    public async Task SaveSessionAsync_ServiceUnreachable_PropagatesForOutboxRetry()
    {
        var handler = new StubHttpMessageHandler(_ => throw new HttpRequestException("connection refused"));
        var client = BuildClient(handler);

        await Assert.ThrowsAsync<HttpRequestException>(() => client.SaveSessionAsync(
            new ConversationSession { ConversationId = "conv-5", CreatedAt = DateTimeOffset.UtcNow, LastMessageAt = DateTimeOffset.UtcNow },
            CancellationToken.None));
    }

    [Fact]
    public async Task AppendMessageAsync_Success_SendsTenantIdRoleAndContent()
    {
        HttpRequestMessage? captured = null;
        var handler = new StubHttpMessageHandler(request =>
        {
            captured = request;
            return new HttpResponseMessage(HttpStatusCode.Created);
        });
        var client = BuildClient(handler);

        await client.AppendMessageAsync("conv-6", "user", "Ola", "wamid.1", CancellationToken.None);

        Assert.Equal(1, handler.CallCount);
        Assert.EndsWith("/conversations/conv-6/messages", captured!.RequestUri!.ToString());
        var body = await captured.Content!.ReadAsStringAsync();
        Assert.Contains("\"tenantId\"", body);
        Assert.Contains("\"role\":\"user\"", body);
        Assert.Contains("\"text\":\"Ola\"", body);
        Assert.Contains("\"externalMessageId\":\"wamid.1\"", body);
    }

    [Fact]
    public async Task AppendMessageAsync_ServiceUnreachable_PropagatesForOutboxRetry()
    {
        var handler = new StubHttpMessageHandler(_ => throw new HttpRequestException("connection refused"));
        var client = BuildClient(handler);

        await Assert.ThrowsAsync<HttpRequestException>(() =>
            client.AppendMessageAsync("conv-7", "assistant", "Oi!", null, CancellationToken.None));
    }

    private static HttpContent JsonContent(string json) =>
        new StringContent(json, System.Text.Encoding.UTF8, "application/json");

    private static IConversationMemoryClient BuildClient(StubHttpMessageHandler handler)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<TimeProvider>(new FakeTimeProvider(DateTimeOffset.UtcNow));
        services.AddSingleton(Options.Create(new ConversationMemoryOptions()));

        // TenantContext is ambient (AsyncLocal), scoped via Push. The pushed scope is
        // deliberately left open (never disposed) for the rest of the test method's async
        // flow - fine for a short-lived unit test, and avoids threading a tenant scope
        // through every call site that doesn't otherwise need one.
        var tenantContext = new TenantContext();
        tenantContext.Push("00000000-0000-0000-0000-000000000001");
        services.AddSingleton(tenantContext);
        services.AddSingleton(new PlatformMetrics());

        services.AddHttpClient<IConversationMemoryClient, ConversationMemoryClient>(client =>
            {
                client.BaseAddress = new Uri("http://localhost/");
            })
            .ConfigurePrimaryHttpMessageHandler(() => handler);

        return services.BuildServiceProvider().GetRequiredService<IConversationMemoryClient>();
    }
}
