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
    public Task SendReplyAsync(
        string conversationId,
        string replyText,
        string idempotencyKey,
        CancellationToken cancellationToken) =>
        SendAsync(
            conversationId,
            new { To = conversationId, Type = "text", Text = replyText },
            "text",
            idempotencyKey,
            cancellationToken);

    public Task SendMenuAsync(
        string conversationId,
        string bodyText,
        IReadOnlyList<MenuOption> options,
        string idempotencyKey,
        CancellationToken cancellationToken) =>
        SendAsync(
            conversationId,
            new
            {
                To = conversationId,
                Type = "interactive",
                Text = bodyText,
                Buttons = options.Select(o => new { o.Id, o.Title }).ToArray()
            },
            "menu",
            idempotencyKey,
            cancellationToken);

    private async Task SendAsync(
        string conversationId,
        object payload,
        string messageKind,
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/internal/messages")
        {
            Content = JsonContent.Create(payload)
        };
        request.Headers.TryAddWithoutValidation("Idempotency-Key", idempotencyKey);
        using var response = await httpClient.SendAsync(request, cancellationToken);

        if (response.IsSuccessStatusCode)
        {
            metrics.Increment("orchestrator_channel_replies_total", ("outcome", "success"), ("type", messageKind));
            return;
        }

        if (await IsPermanentFailureAsync(response, cancellationToken))
        {
            metrics.Increment("orchestrator_channel_replies_total", ("outcome", "permanent_failure"), ("type", messageKind));
            throw new NonRetryableDispatchException(
                $"whatsapp-bff reported the {messageKind} message to {conversationId} as non-retryable ({(int)response.StatusCode}).");
        }

        metrics.Increment("orchestrator_channel_replies_total", ("outcome", "downstream_error"), ("type", messageKind));
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
