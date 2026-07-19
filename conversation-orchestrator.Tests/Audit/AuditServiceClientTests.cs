using System.Net;
using Microsoft.Extensions.DependencyInjection;
using conversation_orchestrator.Tests.Testing;
using Xunit;
using conversation_orchestrator.Application.Ports.Outbound;
using conversation_orchestrator.Adapters.Outbound.Http;
using conversation_orchestrator.Platform;

namespace conversation_orchestrator.Tests.Audit;

public class AuditServiceClientTests
{
    [Fact]
    public async Task RecordJourneyEventAsync_Success_CallsAuditServiceOnce()
    {
        var handler = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        var client = BuildClient(handler);

        await client.RecordJourneyEventAsync(
            new JourneyAuditEvent
            {
                ConversationId = "5511999990000",
                Intent = "debt_renegotiation",
                Outcome = "processed",
                Timestamp = DateTimeOffset.UtcNow
            },
            "audit:test-1",
            CancellationToken.None);

        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public async Task RecordJourneyEventAsync_ServiceUnreachable_PropagatesForOutboxRetry()
    {
        var handler = new StubHttpMessageHandler(_ => throw new HttpRequestException("connection refused"));
        var client = BuildClient(handler);

        await Assert.ThrowsAsync<HttpRequestException>(() => client.RecordJourneyEventAsync(
            new JourneyAuditEvent
            {
                ConversationId = "5511999990000",
                Outcome = "handoff",
                Timestamp = DateTimeOffset.UtcNow
            },
            "audit:test-2",
            CancellationToken.None));
    }

    private static IAuditServiceClient BuildClient(StubHttpMessageHandler handler)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(new PlatformMetrics());
        services.AddHttpClient<IAuditServiceClient, AuditServiceClient>(client =>
            {
                client.BaseAddress = new Uri("http://localhost/");
            })
            .ConfigurePrimaryHttpMessageHandler(() => handler);

        return services.BuildServiceProvider().GetRequiredService<IAuditServiceClient>();
    }
}
