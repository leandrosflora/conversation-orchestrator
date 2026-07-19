using System.Net.Http.Json;
using conversation_orchestrator.Application.Ports.Outbound;
using conversation_orchestrator.Platform;

namespace conversation_orchestrator.Adapters.Outbound.Http;

public class ChannelReplyClient(
    HttpClient httpClient,
    PlatformMetrics metrics) : IChannelReplyClient
{
    public async Task SendReplyAsync(
        string conversationId,
        string replyText,
        CancellationToken cancellationToken)
    {
        using var response = await httpClient.PostAsJsonAsync(
            "/internal/messages",
            new { To = conversationId, Type = "text", Text = replyText },
            cancellationToken);
        metrics.Increment(
            "orchestrator_channel_replies_total",
            ("outcome", response.IsSuccessStatusCode ? "success" : "downstream_error"));
        response.EnsureSuccessStatusCode();
    }
}
