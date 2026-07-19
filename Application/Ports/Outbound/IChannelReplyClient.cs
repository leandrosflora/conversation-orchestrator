namespace conversation_orchestrator.Application.Ports.Outbound;

public interface IChannelReplyClient
{
    Task SendReplyAsync(
        string conversationId,
        string replyText,
        string idempotencyKey,
        CancellationToken cancellationToken);
}
