using System.Net.Http.Json;
using conversation_orchestrator.Application.Ports.Outbound;

namespace conversation_orchestrator.Adapters.Outbound.Http;

public class ChannelReplyClient(HttpClient httpClient, ILogger<ChannelReplyClient> logger) : IChannelReplyClient
{
    public async Task SendReplyAsync(string conversationId, string replyText, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await httpClient.PostAsJsonAsync(
                "/internal/messages",
                new { To = conversationId, Type = "text", Text = replyText },
                cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning(
                    "Channel BFF responded with non-success status {StatusCode} delivering reply for conversation {ConversationId}",
                    response.StatusCode,
                    conversationId);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                ex, "Failed to deliver reply via the Channel BFF for conversation {ConversationId}", conversationId);
        }
    }
}
