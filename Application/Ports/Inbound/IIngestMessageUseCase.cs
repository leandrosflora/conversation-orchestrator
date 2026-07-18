using conversation_orchestrator.Domain;

namespace conversation_orchestrator.Application.Ports.Inbound;

public enum IngestMessageResult
{
    Accepted,
    AlreadyCompleted,
    InProgress
}

public interface IIngestMessageUseCase
{
    Task<IngestMessageResult> ExecuteAsync(
        InboundChannelMessage message,
        CancellationToken cancellationToken);
}
