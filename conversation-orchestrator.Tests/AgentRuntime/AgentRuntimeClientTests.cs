using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using conversation_orchestrator.Tests.Testing;
using Xunit;
using conversation_orchestrator.Application.Ports.Outbound;
using conversation_orchestrator.Adapters.Outbound.Http;
using conversation_orchestrator.Platform;

namespace conversation_orchestrator.Tests.AgentRuntime;

public class AgentRuntimeClientTests
{
    [Fact]
    public async Task ProcessAsync_SuccessResponse_ReturnsParsedResult()
    {
        var expected = new AgentRuntimeResult
        {
            Intent = "debt_renegotiation",
            Confidence = 0.9,
            ReplyText = "Vamos te ajudar a renegociar.",
            RequiresHandoff = false
        };
        var handler = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(expected)
        });
        var client = BuildClient(handler);

        var result = await client.ProcessAsync(SampleRequest(), CancellationToken.None);

        Assert.Equal("debt_renegotiation", result.Intent);
        Assert.False(result.RequiresHandoff);
        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public async Task ProcessAsync_SerializesRequestUsingExactPascalCasePropertyNames()
    {
        // agent-runtime-renegotiation's Pydantic model only accepts the exact PascalCase
        // alias (ConversationId, not conversationId) - PostAsJsonAsync defaults to camelCase
        // unless given explicit options, which silently breaks this contract. Regression
        // test for that: https://github.com internal incident, 422 on every /process call.
        string? capturedBody = null;
        var handler = new StubHttpMessageHandler(request =>
        {
            capturedBody = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new AgentRuntimeResult { RequiresHandoff = false })
            };
        });
        var client = BuildClient(handler);

        await client.ProcessAsync(SampleRequest(), CancellationToken.None);

        Assert.NotNull(capturedBody);
        Assert.Contains("\"ConversationId\"", capturedBody);
        Assert.Contains("\"MessageType\"", capturedBody);
        Assert.DoesNotContain("\"conversationId\"", capturedBody);
        Assert.DoesNotContain("\"messageType\"", capturedBody);
    }

    [Fact]
    public async Task ProcessAsync_TransientFailureThenSuccess_RetriesAndReturnsResult()
    {
        var expected = new AgentRuntimeResult { Intent = "faq", RequiresHandoff = false };
        var handler = new StubHttpMessageHandler(
            _ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable),
            _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(expected) });
        var client = BuildClient(handler, maxRetryAttempts: 2);

        var result = await client.ProcessAsync(SampleRequest(), CancellationToken.None);

        Assert.Equal("faq", result.Intent);
        Assert.True(handler.CallCount >= 2);
    }

    [Fact]
    public async Task ProcessAsync_Unreachable_ReturnsUnavailableSentinelWithoutThrowing()
    {
        var handler = new StubHttpMessageHandler(_ => throw new HttpRequestException("connection refused"));
        var client = BuildClient(handler);

        var result = await client.ProcessAsync(SampleRequest(), CancellationToken.None);

        Assert.True(result.RequiresHandoff);
        Assert.Equal(AgentRuntimeResult.AgentRuntimeUnavailableReason, result.HandoffReason);
        Assert.Null(result.Intent);
    }

    private static IAgentRuntimeClient BuildClient(StubHttpMessageHandler handler, int maxRetryAttempts = 0)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(new PlatformMetrics());

        var httpClientBuilder = services.AddHttpClient<IAgentRuntimeClient, AgentRuntimeClient>(client =>
        {
            client.BaseAddress = new Uri("http://localhost/");
        });
        httpClientBuilder.ConfigurePrimaryHttpMessageHandler(() => handler);

        if (maxRetryAttempts > 0)
        {
            httpClientBuilder.AddStandardResilienceHandler(options =>
            {
                options.Retry.MaxRetryAttempts = maxRetryAttempts;
                options.Retry.Delay = TimeSpan.FromMilliseconds(10);
                options.Retry.BackoffType = Polly.DelayBackoffType.Constant;
                options.AttemptTimeout.Timeout = TimeSpan.FromSeconds(5);
                options.TotalRequestTimeout.Timeout = TimeSpan.FromSeconds(10);
                options.CircuitBreaker.MinimumThroughput = int.MaxValue;
            });
        }

        var provider = services.BuildServiceProvider();
        return provider.GetRequiredService<IAgentRuntimeClient>();
    }

    private static AgentRuntimeRequest SampleRequest() => new()
    {
        TenantId = "00000000-0000-0000-0000-000000000001",
        ConversationId = "5511999990000",
        MessageType = "Text",
        Text = "Ola",
        JourneyStage = "started"
    };
}
