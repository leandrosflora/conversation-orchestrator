namespace conversation_orchestrator.Tests.Testing;

internal class StubHttpMessageHandler(params Func<HttpRequestMessage, HttpResponseMessage>[] responders) : HttpMessageHandler
{
    public int CallCount { get; private set; }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var responder = responders[Math.Min(CallCount, responders.Length - 1)];
        CallCount++;
        return Task.FromResult(responder(request));
    }
}
