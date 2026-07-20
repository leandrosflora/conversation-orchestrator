using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using conversation_orchestrator.Tests.Testing;
using Xunit;
using conversation_orchestrator.Application.Outbox;
using conversation_orchestrator.Application.Ports.Outbound;
using conversation_orchestrator.Adapters.Outbound.Http;
using conversation_orchestrator.Platform;

namespace conversation_orchestrator.Tests.ChannelBff;

public class ChannelReplyClientTests
{
    [Fact]
    public async Task SendReplyAsync_Success_CallsChannelBffOnce()
    {
        var handler = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        var client = BuildClient(handler);

        await client.SendReplyAsync("5511999990000", "Olá!", "idem-key-1", CancellationToken.None);

        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public async Task SendReplyAsync_ChannelBffUnreachable_PropagatesForOutboxRetry()
    {
        var handler = new StubHttpMessageHandler(_ => throw new HttpRequestException("connection refused"));
        var client = BuildClient(handler);

        await Assert.ThrowsAsync<HttpRequestException>(() =>
            client.SendReplyAsync("5511999990000", "Olá!", "idem-key-2", CancellationToken.None));
    }

    [Fact]
    public async Task SendReplyAsync_AmbiguousBadGateway_PropagatesForOutboxRetry()
    {
        // whatsapp-bff returns 502 without a `retryable` field when the provider call's outcome
        // is ambiguous (see whatsapp-bff's OutboundMessageEndpoints.cs) - that's still worth
        // retrying, unlike the settled 409 case below.
        var handler = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.BadGateway)
        {
            Content = JsonContent.Create(new { error = "Outbound delivery outcome is ambiguous.", reconciliationRequired = true })
        });
        var client = BuildClient(handler);

        await Assert.ThrowsAsync<HttpRequestException>(() =>
            client.SendReplyAsync("5511999990000", "Olá!", "idem-key-3", CancellationToken.None));
    }

    [Fact]
    public async Task SendReplyAsync_SettledNonRetryableConflict_ThrowsNonRetryableDispatchException()
    {
        // whatsapp-bff returns 409 with retryable:false once a prior delivery attempt's
        // reservation is already settled/in-progress (e.g. a permanently rejected recipient) -
        // OutboxDispatcherService must stop retrying this, not treat it like a transient error.
        var handler = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.Conflict)
        {
            Content = JsonContent.Create(new
            {
                error = "Outbound delivery is already in progress or has an ambiguous outcome.",
                retryable = false,
                reconciliationRequired = true
            })
        });
        var client = BuildClient(handler);

        await Assert.ThrowsAsync<NonRetryableDispatchException>(() =>
            client.SendReplyAsync("5511999990000", "Olá!", "idem-key-4", CancellationToken.None));
    }

    private static IChannelReplyClient BuildClient(StubHttpMessageHandler handler)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(new PlatformMetrics());
        services.AddHttpClient<IChannelReplyClient, ChannelReplyClient>(client =>
            {
                client.BaseAddress = new Uri("http://localhost/");
            })
            .ConfigurePrimaryHttpMessageHandler(() => handler);

        return services.BuildServiceProvider().GetRequiredService<IChannelReplyClient>();
    }
}
