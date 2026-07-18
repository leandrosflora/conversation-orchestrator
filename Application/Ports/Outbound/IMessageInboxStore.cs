namespace conversation_orchestrator.Application.Ports.Outbound;

public enum InboxAcquireResult
{
    Acquired,
    InProgress,
    Completed
}

public interface IMessageInboxStore
{
    Task<InboxAcquireResult> TryAcquireAsync(
        string messageId,
        string conversationId,
        CancellationToken cancellationToken);

    Task MarkCompletedAsync(string messageId, CancellationToken cancellationToken);

    Task MarkFailedAsync(string messageId, string errorType, CancellationToken cancellationToken);
}
