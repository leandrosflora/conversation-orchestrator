using conversation_orchestrator.Domain;

namespace conversation_orchestrator.Application.Ports.Inbound;

public interface IIngestMessageUseCase
{
    Task ExecuteAsync(InboundChannelMessage message, CancellationToken cancellationToken);
}
