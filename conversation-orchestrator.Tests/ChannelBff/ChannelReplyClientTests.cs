using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using conversation_orchestrator.Tests.Testing;
using Xunit;
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

        await client.SendReplyAsync("5511999990000", "Olá!", CancellationToken.None);

        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public async Task SendReplyAsync_ChannelBffUnreachable_DoesNotThrow()
    {
        var handler = new StubHttpMessageHandler(_ => throw new HttpRequestException("connection refused"));
        var client = BuildClient(handler);

        var exception = await Record.ExceptionAsync(() =>
            client.SendReplyAsync("5511999990000", "Olá!", CancellationToken.None));

        Assert.Null(exception);
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
