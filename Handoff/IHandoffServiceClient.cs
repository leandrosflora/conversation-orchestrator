using conversation_orchestrator.Models.Handoff;

namespace conversation_orchestrator.Handoff;

public interface IHandoffServiceClient
{
    /// <summary>Never throws; logs and returns on failure to reach the Handoff Service.</summary>
    Task RequestHandoffAsync(HandoffRequest request, CancellationToken cancellationToken);
}
