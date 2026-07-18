using System.Net.Http.Json;
using System.Text.Json.Serialization;
using conversation_orchestrator.Application.Ports.Outbound;
using conversation_orchestrator.Domain;
using conversation_orchestrator.Platform;

namespace conversation_orchestrator.Adapters.Outbound.Http;

public class ConversationMemoryClient(
    HttpClient httpClient,
    TenantContext tenantContext,
    TimeProvider timeProvider,
    PlatformMetrics metrics,
    ILogger<ConversationMemoryClient> logger) : IConversationMemoryClient
{
    public async Task<ConversationSession> GetOrCreateSessionAsync(
        string conversationId,
        CancellationToken cancellationToken)
    {
        try
        {
            using var response = await httpClient.GetAsync($"/sessions/{conversationId}", cancellationToken);

            if (response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadFromJsonAsync<SessionResponseDto>(cancellationToken);
                if (body?.Data is not null)
                {
                    var journeyStage = Enum.TryParse<JourneyStage>(
                        body.Data.JourneyStage,
                        ignoreCase: true,
                        out var parsed)
                        ? parsed
                        : JourneyStage.Started;

                    metrics.Increment(
                        "orchestrator_memory_operations_total",
                        ("operation", "get_session"),
                        ("outcome", "success"));

                    return new ConversationSession
                    {
                        ConversationId = conversationId,
                        CreatedAt = body.Data.CreatedAt,
                        LastMessageAt = timeProvider.GetUtcNow(),
                        JourneyStage = journeyStage,
                        LastIntent = body.Data.LastIntent
                    };
                }

                metrics.Increment(
                    "orchestrator_memory_operations_total",
                    ("operation", "get_session"),
                    ("outcome", "empty"));
            }
            else if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                metrics.Increment(
                    "orchestrator_memory_operations_total",
                    ("operation", "get_session"),
                    ("outcome", "not_found"));
            }
            else
            {
                metrics.Increment(
                    "orchestrator_memory_operations_total",
                    ("operation", "get_session"),
                    ("outcome", "downstream_error"));
                logger.LogWarning(
                    "conversation-memory-service responded with {StatusCode} fetching session for conversation {ConversationId}",
                    response.StatusCode,
                    conversationId);
            }
        }
        catch (Exception ex)
        {
            metrics.Increment(
                "orchestrator_memory_operations_total",
                ("operation", "get_session"),
                ("outcome", "exception"));
            logger.LogWarning(ex, "Failed to fetch session for conversation {ConversationId}", conversationId);
        }

        var now = timeProvider.GetUtcNow();
        return new ConversationSession
        {
            ConversationId = conversationId,
            CreatedAt = now,
            LastMessageAt = now,
            JourneyStage = JourneyStage.Started
        };
    }

    public async Task SaveSessionAsync(
        ConversationSession session,
        CancellationToken cancellationToken)
    {
        try
        {
            var payload = new SessionPutRequestDto
            {
                Data = new SessionDataDto
                {
                    CreatedAt = session.CreatedAt,
                    JourneyStage = session.JourneyStage.ToString(),
                    LastIntent = session.LastIntent
                }
            };

            using var response = await httpClient.PutAsJsonAsync(
                $"/sessions/{session.ConversationId}",
                payload,
                cancellationToken);

            metrics.Increment(
                "orchestrator_memory_operations_total",
                ("operation", "save_session"),
                ("outcome", response.IsSuccessStatusCode ? "success" : "downstream_error"));

            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning(
                    "conversation-memory-service responded with {StatusCode} saving session for conversation {ConversationId}",
                    response.StatusCode,
                    session.ConversationId);
            }
        }
        catch (Exception ex)
        {
            metrics.Increment(
                "orchestrator_memory_operations_total",
                ("operation", "save_session"),
                ("outcome", "exception"));
            logger.LogWarning(ex, "Failed to save session for conversation {ConversationId}", session.ConversationId);
        }
    }

    public async Task AppendMessageAsync(
        string conversationId,
        string role,
        string text,
        string? externalMessageId,
        CancellationToken cancellationToken)
    {
        try
        {
            var payload = new MessageAppendRequestDto
            {
                TenantId = tenantContext.TenantId,
                Role = role,
                Content = new MessageContentDto { Text = text },
                ExternalMessageId = externalMessageId
            };

            using var response = await httpClient.PostAsJsonAsync(
                $"/conversations/{conversationId}/messages",
                payload,
                cancellationToken);

            metrics.Increment(
                "orchestrator_memory_operations_total",
                ("operation", "append_message"),
                ("outcome", response.IsSuccessStatusCode ? "success" : "downstream_error"));

            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning(
                    "conversation-memory-service responded with {StatusCode} appending a {Role} message for conversation {ConversationId}",
                    response.StatusCode,
                    role,
                    conversationId);
            }
        }
        catch (Exception ex)
        {
            metrics.Increment(
                "orchestrator_memory_operations_total",
                ("operation", "append_message"),
                ("outcome", "exception"));
            logger.LogWarning(
                ex,
                "Failed to append a {Role} message for conversation {ConversationId}",
                role,
                conversationId);
        }
    }

    private class SessionPutRequestDto
    {
        [JsonPropertyName("data")]
        public required SessionDataDto Data { get; init; }
    }

    private class SessionResponseDto
    {
        [JsonPropertyName("data")]
        public SessionDataDto? Data { get; init; }
    }

    private class SessionDataDto
    {
        [JsonPropertyName("createdAt")]
        public DateTimeOffset CreatedAt { get; init; }

        [JsonPropertyName("journeyStage")]
        public string JourneyStage { get; init; } = "Started";

        [JsonPropertyName("lastIntent")]
        public string? LastIntent { get; init; }
    }

    private class MessageAppendRequestDto
    {
        [JsonPropertyName("tenantId")]
        public required string TenantId { get; init; }

        [JsonPropertyName("role")]
        public required string Role { get; init; }

        [JsonPropertyName("content")]
        public required MessageContentDto Content { get; init; }

        [JsonPropertyName("externalMessageId")]
        public string? ExternalMessageId { get; init; }
    }

    private class MessageContentDto
    {
        [JsonPropertyName("text")]
        public required string Text { get; init; }
    }
}
