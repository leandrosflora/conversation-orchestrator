namespace conversation_orchestrator.Domain;

/// <summary>
/// Mirrors whatsapp-bff's canonical InboundChannelMessage exactly, including property casing and
/// the fact that whatsapp-bff's OrchestratorClient serializes this with plain System.Text.Json
/// defaults (PascalCase names, enums as integers) — not the ASP.NET Core "Web" JSON defaults.
/// Properties are intentionally nullable so this endpoint can validate and reject with a 400
/// rather than relying on JSON binding to throw.
/// </summary>
public class InboundChannelMessage
{
    public string? MessageId { get; init; }
    public string? From { get; init; }
    public string? ConversationId { get; init; }
    public ChannelMessageType Type { get; init; }
    public string? Text { get; init; }
    public InteractiveReply? Interactive { get; init; }
    public string? RawPayload { get; init; }
    public DateTimeOffset ReceivedAt { get; init; }
}
