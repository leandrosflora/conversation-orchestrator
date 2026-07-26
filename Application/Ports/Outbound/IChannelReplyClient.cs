namespace conversation_orchestrator.Application.Ports.Outbound;

public interface IChannelReplyClient
{
    Task SendReplyAsync(
        string conversationId,
        string replyText,
        string idempotencyKey,
        CancellationToken cancellationToken);

    /// <summary>Sends a WhatsApp interactive-button message (the flow-selection menu) - see
    /// agent-skill-registry.</summary>
    Task SendMenuAsync(
        string conversationId,
        string bodyText,
        IReadOnlyList<MenuOption> options,
        string idempotencyKey,
        CancellationToken cancellationToken);
}
