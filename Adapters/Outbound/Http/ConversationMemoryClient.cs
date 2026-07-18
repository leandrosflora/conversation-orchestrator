using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using conversation_orchestrator.Application.Ports.Outbound;
using conversation_orchestrator.Configuration;
using conversation_orchestrator.Domain;

namespace conversation_orchestrator.Adapters.Outbound.Http;

public class ConversationMemoryClient(
    HttpClient httpClient,
    IOptions<ConversationMemoryOptions> options,
    TimeProvider timeProvider,
    ILogger<ConversationMemoryClient> logger) : IConversationMemoryClient
{
    public async Task<ConversationSession> GetOrCreateSessionAsync(string conversationId, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await httpClient.GetAsync($"/sessions/{conversationId}", cancellationToken);

            if (response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadFromJsonAsync<SessionResponseDto>(cancellationToken);
                if (body?.Data is not null)
                {
                    // A value that doesn't parse (e.g. persisted under the pre-enum "started"/
                    // "processed" scheme) falls back to Started rather than failing the request -
                    // same treatment as a missing session (see journey-state-machine capability).
                    var journeyStage = Enum.TryParse<JourneyStage>(body.Data.JourneyStage, ignoreCase: true, out var parsed)
                        ? parsed
                        : JourneyStage.Started;

                    return new ConversationSession
                    {
                        ConversationId = conversationId,
                        CreatedAt = body.Data.CreatedAt,
                        LastMessageAt = timeProvider.GetUtcNow(),
                        JourneyStage = journeyStage,
                        LastIntent = body.Data.LastIntent
                    };
                }
            }
            else if (response.StatusCode != System.Net.HttpStatusCode.NotFound)
            {
                logger.LogWarning(
                    "conversation-memory-service responded with non-success status {StatusCode} fetching session for conversation {ConversationId}",
                    response.StatusCode,
                    conversationId);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                ex, "Failed to reach conversation-memory-service fetching session for conversation {ConversationId}", conversationId);
        }

        var now = timeProvider.GetUtcNow();
        return new ConversationSession { ConversationId = conversationId, CreatedAt = now, LastMessageAt = now };
    }

    public async Task SaveSessionAsync(ConversationSession session, CancellationToken cancellationToken)
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

            using var response = await httpClient.PutAsJsonAsync($"/sessions/{session.ConversationId}", payload, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning(
                    "conversation-memory-service responded with non-success status {StatusCode} saving session for conversation {ConversationId}",
                    response.StatusCode,
                    session.ConversationId);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                ex, "Failed to reach conversation-memory-service saving session for conversation {ConversationId}", session.ConversationId);
        }
    }

    public async Task AppendMessageAsync(
        string conversationId, string role, string text, string? externalMessageId, CancellationToken cancellationToken)
    {
        try
        {
            var payload = new MessageAppendRequestDto
            {
                TenantId = options.Value.TenantId,
                Role = role,
                Content = new MessageContentDto { Text = text },
                ExternalMessageId = externalMessageId
            };

            using var response = await httpClient.PostAsJsonAsync($"/conversations/{conversationId}/messages", payload, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning(
                    "conversation-memory-service responded with non-success status {StatusCode} appending a {Role} message for conversation {ConversationId}",
                    response.StatusCode,
                    role,
                    conversationId);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                ex,
                "Failed to reach conversation-memory-service appending a {Role} message for conversation {ConversationId}",
                role,
                conversationId);
        }
    }

    // conversation-memory-service's session model has no field aliases (unlike its Mongo-backed
    // message/memory models), so the wire format is the literal snake_case Pydantic field names -
    // JsonPropertyName pins that exactly regardless of any global naming policy. The nested "data"
    // payload is opaque to conversation-memory-service (see its own design), so its inner shape
    // is entirely up to this client; camelCase here is just this client's own choice.
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

    // tenantId/externalMessageId have Pydantic aliases with populate_by_name=True (either casing
    // works), but role/content have no alias at all - those two must be exactly lowercase.
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
