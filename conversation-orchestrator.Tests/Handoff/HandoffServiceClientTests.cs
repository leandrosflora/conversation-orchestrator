using System.Net;
using Microsoft.Extensions.DependencyInjection;
using conversation_orchestrator.Tests.Testing;
using Xunit;
using conversation_orchestrator.Application.Ports.Outbound;
using conversation_orchestrator.Adapters.Outbound.Http;
using conversation_orchestrator.Platform;

namespace conversation_orchestrator.Tests.Handoff;

public class HandoffServiceClientTests
{
    [Fact]
    public async Task RequestHandoffAsync_Success_CallsHandoffServiceOnce()
    {
        var handler = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        var client = BuildClient(handler);

        await client.RequestHandoffAsync(
            new HandoffRequest { ConversationId = "5511999990000", Reason = "low_confidence" },
            "handoff:test-1",
            CancellationToken.None);

        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public async Task RequestHandoffAsync_ServiceUnreachable_PropagatesForOutboxRetry()
    {
        var handler = new StubHttpMessageHandler(_ => throw new HttpRequestException("connection refused"));
        var client = BuildClient(handler);

        await Assert.ThrowsAsync<HttpRequestException>(() => client.RequestHandoffAsync(
            new HandoffRequest { ConversationId = "5511999990000", Reason = "agent_runtime_unavailable" },
            "handoff:test-2",
            CancellationToken.None));
    }

    private static IHandoffServiceClient BuildClient(StubHttpMessageHandler handler)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(new PlatformMetrics());
        services.AddHttpClient<IHandoffServiceClient, HandoffServiceClient>(client =>
            {
                client.BaseAddress = new Uri("http://localhost/");
            })
            .ConfigurePrimaryHttpMessageHandler(() => handler);

        return services.BuildServiceProvider().GetRequiredService<IHandoffServiceClient>();
    }
}
