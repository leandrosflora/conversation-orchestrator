using System.Text.Json;
using conversation_orchestrator.Application.Ports.Outbound;

namespace conversation_orchestrator.Application.Outbox;

public static class OutboxEffectTypes
{
    public const string MemoryAppendMessage = "memory.append_message";
    public const string MemorySaveSession = "memory.save_session";
    public const string ChannelReply = "channel.reply";
    public const string HandoffRequest = "handoff.request";
    public const string AuditRecord = "audit.record";
    public const string IntentDetected = "kafka.intent_detected";
    public const string StateChanged = "kafka.state_changed";
}

public sealed record MemoryAppendMessageEffect(
    string ConversationId,
    string Role,
    string Text,
    string? ExternalMessageId);

public sealed record MemorySaveSessionEffect(
    string ConversationId,
    DateTimeOffset CreatedAt,
    DateTimeOffset LastMessageAt,
    string JourneyStage,
    string? LastIntent);

public sealed record ChannelReplyEffect(string ConversationId, string ReplyText);

public sealed record HandoffRequestEffect(string ConversationId, string Reason);

public sealed record AuditRecordEffect(
    string ConversationId,
    string? Intent,
    string Outcome,
    DateTimeOffset Timestamp);

public sealed record IntentDetectedEffect(
    string ConversationId,
    string Intent,
    double Confidence,
    DateTimeOffset DetectedAt);

public sealed record StateChangedEffect(
    string ConversationId,
    string PreviousStage,
    string NewStage,
    DateTimeOffset ChangedAt);

public static class DurableEffectFactory
{
    public static DurableEffect Create<T>(string effectType, string idempotencyKey, T payload) =>
        new(effectType, idempotencyKey, JsonSerializer.Serialize(payload));
}
