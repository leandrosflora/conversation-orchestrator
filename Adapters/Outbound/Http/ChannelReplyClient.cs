using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using conversation_orchestrator.Application.Outbox;
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
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/internal/messages")
        {
            Content = JsonContent.Create(new { To = conversationId, Type = "text", Text = replyText })
        };
        request.Headers.TryAddWithoutValidation("Idempotency-Key", idempotencyKey);
        using var response = await httpClient.SendAsync(request, cancellationToken);

        if (response.IsSuccessStatusCode)
        {
            metrics.Increment("orchestrator_channel_replies_total", ("outcome", "success"));
            return;
        }

        if (await IsPermanentFailureAsync(response, cancellationToken))
        {
            metrics.Increment("orchestrator_channel_replies_total", ("outcome", "permanent_failure"));
            throw new NonRetryableDispatchException(
                $"whatsapp-bff reported the reply to {conversationId} as non-retryable ({(int)response.StatusCode}).");
        }

        metrics.Increment("orchestrator_channel_replies_total", ("outcome", "downstream_error"));
        response.EnsureSuccessStatusCode();
    }

    private static async Task<bool> IsPermanentFailureAsync(
        HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            var body = await response.Content.ReadFromJsonAsync<DeliveryErrorBody>(cancellationToken);
            return body?.Retryable == false;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private sealed record DeliveryErrorBody([property: JsonPropertyName("retryable")] bool? Retryable);
}
