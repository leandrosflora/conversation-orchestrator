namespace conversation_orchestrator.Application.Ports.Outbound;

public interface IChannelReplyClient
{
    /// <summary>Never throws; logs and returns on failure to reach the Channel BFF.</summary>
    Task SendReplyAsync(string conversationId, string replyText, CancellationToken cancellationToken);
}
